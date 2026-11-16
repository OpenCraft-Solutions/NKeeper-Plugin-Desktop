using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NKeeper.Testing;
using Npgsql;

namespace NKeeper.Plugins.Desktop.Tests;

/// <summary>
/// docs/architecture.md §7.20, §7.21: the desktop plugin, opening an RDP session through a real guacd to a real xrdp
/// server, with the server's certificate trusted in-band and guacd's instructions relayed to the browser.
/// </summary>
public sealed class DesktopTests(PostgresFixture postgres, RdpServerFixture rdp) : IClassFixture<PostgresFixture>, IClassFixture<RdpServerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The test server, with this plugin's built package and the given guacd, and its administrator's session cookie.</summary>
    private async Task<(TestServer Server, TestServerClient Client, string Cookie, string ConnectionString)> StartAsync(string guacdAddress)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new TestServerOptions { ConnectionString = connectionString };
        options.Settings["Guacd:Address"] = guacdAddress;
        options.PluginPackages.Add(Assert.Single(DevelopmentKeys.BuiltPackages(), p => Path.GetFileName(p).StartsWith("desktop-", StringComparison.Ordinal)));
        var server = await TestServer.StartAsync(options, Token);
        var client = server.CreateClient();
        using var login = await client.SendAsync(HttpMethod.Post, "/api/auth/login", new { userNameOrEmail = TestServer.AdminUserName, password = TestServer.AdminPassword, rememberMe = false }, Token);
        login.EnsureSuccessStatusCode();
        var cookie = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        return (server, client, cookie, connectionString);
    }

    [Fact]
    public async Task A_desktop_opens_once_its_certificate_is_trusted_and_relays_guacd()
    {
        var (server, client, cookie, connectionString) = await StartAsync(rdp.GuacdAddress);
        await using var _ = server;
        using var __ = client;
        using var created = await client.SendAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "xrdp",
            ipAddress = RdpServerFixture.Address,
            services = new[] { new { type = "rdp", port = rdp.Port, settings = new { security = "tls" } } },
            credentials = new[] { new { username = RdpServerFixture.UserName, password = RdpServerFixture.Password } },
        }, Token);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Token));
        var host = await created.Content.ReadFromJsonAsync<JsonElement>(Token);

        await using var desktop = await SessionClient.ConnectAsync(server, cookie, "rdp");
        await desktop.SendAsync(new
        {
            type = "start",
            hostId = host.GetProperty("id").GetGuid(),
            serviceId = host.GetProperty("services")[0].GetProperty("id").GetGuid(),
            credentialId = host.GetProperty("credentials")[0].GetProperty("id").GetGuid(),
            options = new { width = 1024, height = 768, dpi = 96 },
        });

        // The server's certificate, in-band: a certificate this time, with its subject.
        var prompt = await desktop.ExpectAsync("hostKey");
        Assert.Equal(("tls-certificate", "x509"), (prompt.GetProperty("kind").GetString(), prompt.GetProperty("algorithm").GetString()));
        Assert.False(string.IsNullOrEmpty(prompt.GetProperty("subject").GetString()));
        await desktop.SendAsync(new { type = "hostKeyDecision", trust = true });
        await desktop.ExpectAsync("connected");

        // guacd's "ready" first, then the remote desktop's drawing.
        await desktop.ReadTextUntilAsync("5.ready,");
        await desktop.SendTextAsync("4.size,4.1024,3.768,2.96;");
        await desktop.ReadTextUntilAsync(".img,");

        var trusted = await client.Http.GetFromJsonAsync<JsonElement>(new Uri($"/api/hosts/{host.GetProperty("id").GetGuid()}", UriKind.Relative), Token);
        Assert.Equal("tls-certificate", Assert.Single(trusted.GetProperty("trustedKeys").EnumerateArray()).GetProperty("kind").GetString());
        Assert.Contains(await AuditDetailsAsync(connectionString, "credential.use"), d => d.StartsWith("rdp to service ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_guacd_the_desktop_says_the_gateway_is_unavailable()
    {
        var (server, client, cookie, _) = await StartAsync("127.0.0.1:1");
        await using var _server = server;
        using var _client = client;
        using var created = await client.SendAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "xrdp",
            ipAddress = RdpServerFixture.Address,
            services = new[] { new { type = "rdp", port = rdp.Port, settings = new { security = "tls" } } },
            credentials = new[] { new { username = RdpServerFixture.UserName, password = RdpServerFixture.Password } },
        }, Token);
        var host = await created.Content.ReadFromJsonAsync<JsonElement>(Token);
        var start = new
        {
            type = "start",
            hostId = host.GetProperty("id").GetGuid(),
            serviceId = host.GetProperty("services")[0].GetProperty("id").GetGuid(),
            credentialId = host.GetProperty("credentials")[0].GetProperty("id").GetGuid(),
        };

        // Trusting the certificate comes first; then guacd is found missing.
        await using var desktop = await SessionClient.ConnectAsync(server, cookie, "rdp");
        await desktop.SendAsync(start);
        await desktop.ExpectAsync("hostKey");
        await desktop.SendAsync(new { type = "hostKeyDecision", trust = true });
        Assert.Equal("gateway_unavailable", (await desktop.ExpectAsync("error")).GetProperty("code").GetString());
    }

    /// <summary>The details of the audit events with this action, read from the server's own database: there's no API for the audit trail yet.</summary>
    private static async Task<List<string>> AuditDetailsAsync(string connectionString, string action)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand("SELECT detail FROM core.audit_events WHERE action = @action", connection);
        command.Parameters.AddWithValue("action", action);
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            details.Add(reader.GetString(0));
        }

        return details;
    }
}
