using System.IO;
using System.Text.Json.Nodes;
using SubTerra.Services;
using SubTerra.Simulation;
using SubTerra.ViewModels;
using Xunit;

namespace SubTerra.Tests;

public sealed class AdminAccessTests
{
    private const string Password = "test-only passphrase 927";

    [Fact]
    public Task SuccessfulLoginUpdatesToolPermissionsAndLogoutRevokesThem() => ScanControlTests.OnSta(async () =>
    {
        var path = TemporaryPath();
        try
        {
            var access = new AdminAccessService(path);
            using var vm = new MainViewModel(new SimulatedScanSource(), new SessionStorage(), access: access);
            Assert.False(vm.CanStart);
            Assert.True((await access.CreateAdministratorAsync(Password)).Success);
            Assert.True(vm.CanStart);
            Assert.True(vm.CanEditSettings);
            Assert.True(await vm.LogoutAsync());
            Assert.False(vm.CanStart);
            Assert.True((await access.LoginAsync("admin", Password)).Success);
            Assert.True(vm.CanStart);
        }
        finally { File.Delete(path); }
    });

    [Fact]
    public async Task FirstSetupStoresHashAndLoginDoesNotSurviveRestart()
    {
        var path = TemporaryPath();
        try
        {
            var access = new AdminAccessService(path);
            Assert.False(access.IsAdministrator);
            Assert.False((await access.LoginAsync("admin", Password)).Success);
            Assert.False((await access.CreateAdministratorAsync("short")).Success);
            Assert.False(File.Exists(path));
            Assert.True((await access.CreateAdministratorAsync(Password)).Success);
            Assert.True(access.IsAdministrator);
            var content = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(Password, content);
            var account = JsonNode.Parse(content)!;
            Assert.Equal(600_000, account["Iterations"]!.GetValue<int>());
            Assert.Equal(16, Convert.FromBase64String(account["Salt"]!.GetValue<string>()).Length);
            Assert.Equal(32, Convert.FromBase64String(account["Hash"]!.GetValue<string>()).Length);
            Assert.False((await access.CreateAdministratorAsync("a replacement password")).Success);
            access.Logout();
            Assert.False(access.IsAdministrator);
            var restarted = new AdminAccessService(path);
            Assert.True(restarted.IsConfigured);
            Assert.False(restarted.IsAdministrator);
            Assert.False((await restarted.LoginAsync("guest", Password)).Success);
            Assert.False((await restarted.LoginAsync("admin", "wrong password")).Success);
            Assert.True((await restarted.LoginAsync("admin", Password)).Success);
            Assert.True(restarted.IsAdministrator);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FiveFailuresLockLoginAcrossRestartUntilTimeout()
    {
        var path = TemporaryPath();
        try
        {
            var clock = new TestTime();
            var access = new AdminAccessService(path, clock);
            Assert.True((await access.CreateAdministratorAsync(Password)).Success);
            access.Logout();
            for (var i = 0; i < 5; i++) Assert.False((await access.LoginAsync("admin", "wrong")).Success);
            var restarted = new AdminAccessService(path, clock);
            var locked = await restarted.LoginAsync("admin", Password);
            Assert.False(locked.Success);
            Assert.Contains("30", locked.Message);
            clock.Now = clock.Now.AddSeconds(31);
            Assert.True((await restarted.LoginAsync("admin", Password)).Success);
            restarted.Logout();
            Assert.False((await restarted.LoginAsync("admin", "wrong")).Success);
            Assert.True((await restarted.LoginAsync("admin", Password)).Success);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"Version\":1,\"Login\":\"admin\",\"Iterations\":1}")]
    public async Task CorruptAccountNeverGrantsAccessOrAllowsReplacement(string content)
    {
        var path = TemporaryPath();
        try
        {
            await File.WriteAllTextAsync(path, content);
            var access = new AdminAccessService(path);
            Assert.False((await access.LoginAsync("admin", Password)).Success);
            Assert.False(access.IsAdministrator);
            Assert.False((await access.CreateAdministratorAsync(Password)).Success);
            Assert.Equal(content, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"subterra-auth-{Guid.NewGuid():N}.json");
    private sealed class TestTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
