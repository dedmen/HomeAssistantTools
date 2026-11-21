using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using ZyxelRoot;
using ZyxelRoot.Modem;
using ZyxelRoot.Sms;

namespace HomeAssistantNetDaemon.apps.HassModel.Zyxel
{
    /// <summary>
    /// Owns the long-lived root connection to the Zyxel FWA505 through the
    /// ZyxelRoot library and exposes it to the NetDaemon apps:
    /// <see cref="ZyxelHandler"/> (entities, reboot service) and the KPN apps
    /// (SMS send / 2FA code read).
    /// </summary>
    /// <remarks>
    /// The connection is maintained by <see cref="PersistentRootShell"/>: it
    /// bootstraps the root daemon when needed, survives daemon restarts and
    /// router reboots, and raises <see cref="RouterRebooted"/> when a reconnect
    /// proves the box rebooted. This service re-ensures the watchdog cron line
    /// and re-arms the APN back-off on every such event.
    /// </remarks>
    public sealed class ZyxelRouterService : IHostedService
    {
        // The watchdog line from the flat-l2-zyxel-openwrt migration guide (Phase 3.2).
        private const string DefaultCronLine = "*/5 * * * * /xdata/ra_keep.sh";
        private const string CronMarker = "ra_keep.sh";
        private const string MmmLog = "/var/log/mmm.log";
        private const string CmLog = "/var/log/cm.log";

        // The live-verified kick signature (detect-kick.md §4.4): the account-level
        // single-active enforcement purges the PDN; mmm.log carries both lines.
        private static readonly Regex KickSignature = new(
            @"MIPC_DATA_GET_CALL_REQ FAIL|Reset APN\d*'s PDN info", RegexOptions.Compiled);

        // KPN 2FA SMS format: "Beste klant, voor verificatie in 2 stappen is je code 71354."
        private static readonly Regex TwoFactorCodeRx = new(
            @"is je code\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IConfiguration _cfg;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _switchGate = new(1, 1);
        private readonly PrefixedLog _log;

        private volatile PersistentRootShell? _shell;
        private DataStateMonitor? _dataState;
        private ApnBackoff? _backoff;
        private KickMonitor? _kick;
        private Task? _kickReader;
        private Task? _execution;
        private bool _modemHangOpen;
        private Task? _modemResetTask;
        private TaskCompletionSource<bool> _connectedTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _lteEnabled;

        public ZyxelRouterService(IConfiguration cfg, ILogger<ZyxelRouterService> logger)
        {
            _cfg = cfg;
            _log = new PrefixedLog("[Zyxel]", logger);
        }

        /// <summary>True while a live root shell is attached.</summary>
        public bool IsConnected => _shell is { } shell && shell.IsConnected;

        /// <summary>The last observed data-connection state (seeds the entity).</summary>
        public DataState CurrentDataState => _dataState?.Current ?? DataState.Unknown;

        /// <summary>True while the APN back-off hold is active (seeds the entity).</summary>
        public bool IsHolding => _backoff?.IsHolding ?? false;

        /// <summary>Why the current hold is active (kick, switch off), or null.</summary>
        public string? HoldReason => _backoff?.Reason;

        /// <summary>When the current hold releases, or null when manual/indefinite.</summary>
        public DateTime? HoldUntilUtc => _backoff?.HoldUntilUtc;

        /// <summary>Number of successful reconnects since the original connect.</summary>
        public int ReconnectCount => _shell?.ReconnectCount ?? 0;

        // ------------------------------------------------------------------ events
        // Fired from the poll/maintain loops; handlers must not call back into
        // this service synchronously (the shell's command lock serializes).

        /// <summary>Every data up/down transition (any cause: kick, hold, signal, reboot).</summary>
        public event EventHandler<DataStateEventArgs>? DataStateChanged;

        /// <summary>Every APN back-off hold state change (kick-driven or manual).</summary>
        public event EventHandler<HoldStateEventArgs>? HoldChanged;

        /// <summary>Raised exactly once per drop→reconnect that proves a router reboot.</summary>
        public event EventHandler<RouterRebootEventArgs>? RouterRebooted;

        // ----------------------------------------------------------- hosted service

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _execution = Task.Run(() => ExecuteAsync(_lifetime.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _lifetime.Cancel();
            if (_execution is { } task)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
                catch (TimeoutException) { }
                catch (OperationCanceledException) { }
            }
        }

        private async Task ExecuteAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunCoreAsync(ct).ConfigureAwait(false);
                    return;   // only returns when stopped
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.Warning($"connection cycle failed: {ex.Message} — retrying in 30s");
                }

                try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }

            await CleanupAsync();
        }

        private async Task RunCoreAsync(CancellationToken ct)
        {
            ZyxelSettings settings = LoadSettings();
            string? validation = settings.Validate();
            if (validation is not null)
                throw new ZyxelRootException($"settings: {validation}");

            var shell = new PersistentRootShell(settings, Log);
            _shell = shell;

            // Fired by the maintain loop after a live shell is available again,
            // so it may call RunAsync directly (no deadlock).
            shell.RouterRebooted += async (_, e) =>
            {
                Log($"RouterRebooted: uptime {e.ObservedUptimeSeconds:F0}s, was down {e.DisconnectedFor.TotalSeconds:F0}s");
                try { await EnsureCronJobAsync(checkOnly: false, CancellationToken.None); }
                catch (Exception ex) { _log.Error($"reboot: cron re-ensure failed: {ex.Message}"); }
                try
                {
                    if (_backoff is { } backoff)
                        await backoff.OnRouterRebootedAsync(CancellationToken.None);
                }
                catch (Exception ex) { _log.Error($"reboot: hold re-arm failed: {ex.Message}"); }
                try
                {
                    _lteEnabled = await ReadApnEnabledAsync(CancellationToken.None);
                    Log($"reboot: APN enabled = {_lteEnabled}");
                }
                catch (Exception ex) { _log.Error($"reboot: APN state read failed: {ex.Message}"); }
                RouterRebooted?.Invoke(this, e);
            };

            await shell.ConnectAsync(ct).ConfigureAwait(false);
            Log("root connection established");
            _connectedTcs.TrySetResult(true);

            // watchdog cron line (re-registered on every reboot by the event above)
            try { await EnsureCronJobAsync(checkOnly: false, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.Error($"cron ensure failed: {ex.Message}"); }

            // APN back-off state machine (kick-driven + manual via the switch)
            var backoffState = new ApnBackoff(shell,
                    ParseDuration("Zyxel:Backoff", TimeSpan.FromMinutes(5)), Log)
                {
                    KickThreshold = int.TryParse(_cfg["Zyxel:KickThreshold"], out int threshold) ? threshold : 3,
                    KickWindow = ParseDuration("Zyxel:KickWindow", TimeSpan.FromSeconds(120)),
                    ReconnectGrace = ParseDuration("Zyxel:ReconnectGrace", TimeSpan.FromSeconds(15)),
                };
            backoffState.HoldChanged += (_, e) =>
            {
                Log($"hold {(e.Holding ? "ACTIVE" : "released")}" +
                    (e.Reason is not null ? $" ({e.Reason})" : "") +
                    (e.HoldUntilUtc is { } until
                        ? $" until {until.ToLocalTime():HH:mm:ss}"
                        : (e.Holding ? " until released" : "")));
                HoldChanged?.Invoke(this, e);
            };
            _backoff = backoffState;

            try
            {
                // A previous crashed run may have left the box in OnHold — make
                // sure it is dialing before we start monitoring. GoOn on a box
                // that is NOT held is a verified no-op, so a blind GoOn is the
                // whole safety check.
                await backoffState.StartupGoOnAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Error($"startup safety check failed: {ex.Message} (if the box is still held: 'cmctl send GoOn')");
            }

            // Seed the switch from the device (AP_Enable is persistent config:
            // it survives reboots, so the cached entity state may be stale).
            _lteEnabled = await ReadApnEnabledAsync(ct).ConfigureAwait(false);
            Log($"APN enabled (from device) = {_lteEnabled}");

            // data up/down → binary_sensor.zyxel_lte_connected
            var dataState = new DataStateMonitor(shell);
            dataState.StateChanged += (_, e) =>
            {
                string word = e.State == DataState.Connected ? "CONNECTED" : "DISCONNECTED";
                Log($"data connection: {word}{(e.Interface is null ? "" : $" on {e.Interface}")} — was {e.Previous} — {e.Detail}");
                DataStateChanged?.Invoke(this, e);
            };
            await dataState.StartAsync(ct).ConfigureAwait(false);
            _dataState = dataState;

            // kick → APN back-off (log tails + AT poll; tap only when present)
            bool enableKickBackoff = _cfg.GetValue("Zyxel:EnableKickBackoff", true);
            if (enableKickBackoff)
            {
                bool tapOk = (await shell.RunAsync(
                    "test -x /tmp/mipc_tap && echo tap-ok", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false))
                    .Output.Contains("tap-ok", StringComparison.Ordinal);
                Log(tapOk ? "kick monitor: tap present" : "kick monitor: no tap binary — log tails + AT poll only");

                var kick = new KickMonitor(shell, new KickMonitorOptions
                {
                    PollInterval = TimeSpan.FromSeconds(2),
                    AtCommands = new[] { "AT+CSQ" },   // CREG/CGREG read CME 100 on this modem
                    TapEnabled = tapOk,
                    LogSources = new KickLogSource[]
                    {
                        // The kick detector: every matching line IS a teardown
                        // signature (the filter only passes these).
                        new(MmmLog, KickSignature.ToString()),
                        // Context: cm dial-state changes (verify the re-dial after GoOn).
                        new(CmLog, @"CHANGE STATE"),
                    },
                });
                _kick = kick;

                _kickReader = Task.Run(async () =>
                {
                    _modemHangOpen = false;   // fresh capture: a new hang episode may trigger a reset
                    try
                    {
                        await foreach (KickRecord r in kick.ReadAsync(ct).ConfigureAwait(false))
                        {
                            switch (r.Kind)
                            {
                                case KickRecordKind.Log when r.Label == MmmLog:
                                    // A teardown-signature line (the filter only
                                    // passes these). Route it through the
                                    // debounce; the hold fires only after
                                    // KickThreshold episodes within KickWindow.
                                    try { await backoffState.OnTeardownSignatureAsync(r.Detail ?? r.Text); }
                                    catch (Exception ex) { _log.Error($"back-off failed: {ex.Message}"); }
                                    break;

                                case KickRecordKind.AtPoll:
                                    // Not logged: on this modem the rssi value
                                    // oscillates (14/99) every poll, and a kick
                                    // is proven by the mmm.log signature anyway.
                                    // A completed round trip (anything but the
                                    // timeout summary) means the module is
                                    // answering again — the hang episode is over.
                                    if (r.Text != "timeout")
                                        _modemHangOpen = false;
                                    break;

                                case KickRecordKind.ModemHang:
                                    // The module stopped answering (30 s AT
                                    // timeout) — hard-reset it once per episode.
                                    Log($"MODEM HANG: {r.Text}");
                                    if (!_modemHangOpen)
                                    {
                                        _modemHangOpen = true;
                                        _modemResetTask = Task.Run(ModemHardResetAsync);
                                    }
                                    break;

                                case KickRecordKind.Notice:
                                    Log($"note: {r.Text}");
                                    break;

                                case KickRecordKind.Log:
                                    Log($"{r.Label}: {FirstLine(r.Detail ?? r.Text)}");
                                    break;

                                case KickRecordKind.Tap:
                                    Log($"tap: {FirstLine(r.Text)}");
                                    break;
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                }, CancellationToken.None);

                await kick.StartAsync(ct).ConfigureAwait(false);
                Log("kick monitor started");
            }

            Log("ready — cron ensured, data-state poll running, APN back-off " +
                ParseDuration("Zyxel:Backoff", TimeSpan.FromMinutes(5)).ToString(@"hh\:mm\:ss"));

            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }

        private async Task CleanupAsync()
        {
            // A hard reset in flight must not be abandoned: a module left
            // powered off by an interrupted PowerOff→PowerOn stays down.
            if (_modemResetTask is { IsCompleted: false } reset)
            {
                try { await reset.WaitAsync(TimeSpan.FromSeconds(90)); }
                catch (TimeoutException) { _log.Warning("exit: modem hard reset still running"); }
            }

            try
            {
                if (_kick is { IsRunning: true } kick)
                    await kick.StopAsync(CancellationToken.None);
                if (_kickReader is { } reader && !reader.IsCompleted)
                    await reader.WaitAsync(TimeSpan.FromSeconds(3));
                if (_kick is { } kickAsync)
                    await kickAsync.DisposeAsync();
            }
            catch { /* best-effort; the device state is in the console stream */ }

            try
            {
                // Default: leave the box dialing (a hold outliving the process
                // would silence the WAN until a manual GoOn).
                if (_backoff is { } backoff)
                    await backoff.ShutdownAsync(keepHold: false, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Error($"exit: hold release failed: {ex.Message} (send 'cmctl send GoOn' on the router)");
            }

            try { if (_dataState is { } data) await data.DisposeAsync(); } catch { }
            try { if (_shell is { } shell) await shell.DisposeAsync(); } catch { }
            _shell = null;
            _backoff = null;
            _dataState = null;
            _kick = null;
        }

        // ----------------------------------------------------------------- accessors

        /// <summary>
        /// Completes when the router has been connected at least once (the
        /// maintain loop keeps it connected afterwards; commands then wait out
        /// reconnects themselves).
        /// </summary>
        public Task WaitForConnectionAsync(CancellationToken ct = default)
        {
            using var _ = ct.Register(() => _connectedTcs.TrySetCanceled(ct));
            return _connectedTcs.Task;
        }

        private PersistentRootShell ConnectedShell() =>
            _shell ?? throw new ZyxelRootException("not connected to the router yet");

        public Task<CommandResult> RunAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
            => ConnectedShell().RunAsync(command, timeout, ct);

        // ------------------------------------------------------------------- switch

        /// <summary>
        /// Drives <c>switch.zyxel_lte_enabled</c>:
        /// on  → AP_Enable 1, then GoOn (releases any hold);
        /// off → OnHold (indefinite, no auto-release), then AP_Enable 0
        /// (disconnects the data call; the APN can be turned back on after the
        /// hold is set, and is re-enabled when the switch turns back on).
        /// Serialized; a no-op when already in the requested state.
        /// </summary>
        public async Task SetLteEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            var shell = ConnectedShell();
            await _switchGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_lteEnabled == enabled)
                {
                    Log($"LTE already {(enabled ? "enabled" : "disabled")} — no-op");
                    return;
                }

                if (enabled)
                {
                    CommandResult r = await shell.RunAsync(
                        "cfg cellwan_mapn edit --Index 1 --AP_Enable 1", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                    if (!r.Output.Contains("Command Successful", StringComparison.Ordinal))
                        _log.Warning($"APN enable: unexpected reply: {FirstLine(r.Output)}");
                    if (_backoff is { } backoff)
                        await backoff.ReleaseAsync("LTE switch on", ct).ConfigureAwait(false);
                }
                else
                {
                    if (_backoff is { } backoff)
                        await backoff.HoldAsync("LTE switch off", ct: ct).ConfigureAwait(false);
                    CommandResult r = await shell.RunAsync(
                        "cfg cellwan_mapn edit --Index 1 --AP_Enable 0", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                    if (!r.Output.Contains("Command Successful", StringComparison.Ordinal))
                        _log.Warning($"APN disable: unexpected reply: {FirstLine(r.Output)}");
                }

                _lteEnabled = enabled;
                Log($"LTE {(enabled ? "enabled" : "disabled")} (AP_Enable {(enabled ? 1 : 0)})");
            }
            finally
            {
                _switchGate.Release();
            }
        }

        /// <summary>
        /// Reads the persistent APN enable state from the device
        /// (<c>cfg cellwan_mapn get</c>, line "1 Enabled|Disabled").
        /// </summary>
        public async Task<bool> ReadApnEnabledAsync(CancellationToken ct = default)
        {
            CommandResult r = await ConnectedShell()
                .RunAsync("cfg cellwan_mapn get", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            return Regex.IsMatch(r.Output ?? "", @"1\s*Enabled");
        }

        // ------------------------------------------------------------- apn reconnect

        /// <summary>
        /// Soft-resets the cellular module to force an APN reconnect — the
        /// vendor's own recovery sequence: <c>AT+CFUN=0</c> → 2 s pause →
        /// <c>AT+CFUN=1</c> → <c>ubus send modem_ee</c>. The module stays
        /// powered; cm re-camps and re-dials the APN on its own. Stops and
        /// reports when the module does not answer — no escalation to a power
        /// off, since a fully down module needs a router reboot. Then waits up
        /// to 90 s for the data call to be back
        /// (<see cref="CurrentDataState"/>).
        /// </summary>
        public async Task ReconnectApnAsync(CancellationToken ct = default)
        {
            Log("APN reconnect: radio cycle starting");

            // The vendor's recovery sequence, as one library call
            // (AT+CFUN=0 → pause → AT+CFUN=1 → ubus send modem_ee).
            ModemPowerResult cycle = await new ModemPower(ConnectedShell())
                .RadioCycleAsync(ct: ct).ConfigureAwait(false);
            if (!cycle.Success)
            {
                // The module did not answer: do not escalate to a power off —
                // a fully down module needs a router reboot.
                _log.Error("APN reconnect: radio cycle failed " +
                           $"({(cycle.TimedOut ? "timed out" : FirstLine(cycle.Output))}); " +
                           "stopping without escalating (recovery needs a router reboot)");
                return;
            }

            Log("APN reconnect: radio cycled — waiting for the data call to come back");

            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline && CurrentDataState != DataState.Connected)
                await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

            if (CurrentDataState == DataState.Connected)
                Log("APN reconnect: data call is back up");
            else
                _log.Warning("APN reconnect: radio cycled but the data call is not back after 90 s");
        }

        /// <summary>
        /// The AT path hung (a 30 s poll timeout — a healthy module answers in
        /// milliseconds, the vendor client only blocks this long when the
        /// module stopped answering): hard-reset the module —
        /// <c>--modem_poweroff</c>, a settle, <c>--nw_radio_state_set 1</c>
        /// plus state verification. Runs once per hang episode; the next
        /// successful AT poll closes the episode. A failed power-on means the
        /// module is fully down and has no user-space power-on — the fallback
        /// is a router reboot (<c>ZyxelRouterReboot</c>).
        /// </summary>
        private async Task ModemHardResetAsync()
        {
            try
            {
                Log("modem hard reset starting (power off → on)");
                var power = new ModemPower(ConnectedShell());

                ModemPowerResult off = await power.PowerOffAsync().ConfigureAwait(false);
                if (!off.Success)
                {
                    _log.Error("modem hard reset: power off failed " +
                        $"({(off.TimedOut ? "timed out — the module is not answering at all" : FirstLine(off.Output))}) — " +
                        "recovery needs a router reboot");
                    return;
                }
                Log("modem hard reset: module powered off");

                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                ModemPowerResult on = await power.PowerOnAsync().ConfigureAwait(false);
                if (on.Success)
                    Log("modem hard reset done — radio back ON, waiting for the data call");
                else
                    _log.Error("modem hard reset: power on failed " +
                        $"({(on.TimedOut ? "timed out — no live module-side MIPC peer to power on" : FirstLine(on.Output))}) — " +
                        "the module is fully down; recovery needs a router reboot");
            }
            catch (Exception ex)
            {
                _log.Error($"modem hard reset failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------- reboot

        /// <summary>
        /// Reboots the router (<c>zycli reboot</c>). The connection drops
        /// mid-command; a timeout or closed shell is the success signal, and
        /// the maintain loop (plus <see cref="RouterRebooted"/>) handles the rest.
        /// </summary>
        public async Task RebootAsync(CancellationToken ct = default)
        {
            var shell = ConnectedShell();
            try
            {
                await shell.RunAsync("zycli reboot", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // expected: the router drops the connection while it reboots
            }
            Log("reboot command sent");
        }

        // ----------------------------------------------------------------------- sms

        /// <summary>Sends a text SMS through the router modem (PDU mode).</summary>
        public Task<SmsSendResult> SendSmsAsync(string to, string text, CancellationToken ct = default)
            => new ZyxelSms(ConnectedShell()).SendAsync(to, text, ct: ct);

        /// <summary>
        /// The most recent stored SMS from the router inbox, newest first
        /// (sent records filtered out by default).
        /// </summary>
        public async Task<IReadOnlyList<SmsMessage>> GetRecentSmsAsync(
            int count = 10, bool includeSent = false, CancellationToken ct = default)
        {
            // More files than count are fetched so the result is really the
            // newest count *received* messages.
            return await new ZyxelSms(ConnectedShell())
                .GetStoredAsync(Math.Max(30, count), includeSent, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Waits for the KPN 2FA code in the router's SMS inbox.
        /// </summary>
        /// <remarks>
        /// Finds the most recent <i>received</i> SMS matching
        /// <c>is je code \d+</c> (KPN format: "Beste klant, voor verificatie in
        /// 2 stappen is je code 71354."). When that message is no older than
        /// <paramref name="maxAge"/> it is used immediately; otherwise new inbox
        /// messages are polled (every 3 s) for up to <paramref name="timeout"/>.
        /// Returns <c>null</c> when no fresh code arrived in time — the caller
        /// must fail the login.
        /// </remarks>
        public async Task<string?> WaitForSmsCodeAsync(
            TimeSpan maxAge, TimeSpan timeout, CancellationToken ct = default)
        {
            var shell = ConnectedShell();
            var sms = new ZyxelSms(shell);
            DateTimeOffset now = DateTimeOffset.Now;

            // Snapshot the current inbox: "new" below means delivered after this.
            var known = new HashSet<string>();
            try
            {
                var snapshot = await sms.GetStoredAsync(30, includeSent: false, ct).ConfigureAwait(false);
                foreach (SmsMessage m in snapshot)
                    known.Add(m.File);

                foreach (SmsMessage m in snapshot)   // newest first
                {
                    if (TryExtractTwoFactorCode(m) is not { } code)
                        continue;

                    if (m.TimeStamp is { } ts && now - ts <= maxAge)
                    {
                        Log($"Enter SMS 2FA code: {code} (age {(now - ts).TotalSeconds:F0}s)");
                        return code;
                    }

                    Log($"newest 2FA SMS is {(m.TimeStamp is { } old ? (now - old).TotalSeconds : -1):F0}s old — waiting for a fresh one");
                    break;   // older code: wait for a new message
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.Error($"2FA inbox snapshot failed: {ex.Message}");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            try
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), linked.Token).ConfigureAwait(false);

                    IReadOnlyList<SmsMessage> recent;
                    try
                    {
                        recent = await sms.GetStoredAsync(30, includeSent: false, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _log.Error($"2FA inbox poll failed: {ex.Message}");
                        continue;
                    }

                    foreach (SmsMessage m in recent)   // newest first
                    {
                        if (!known.Add(m.File))
                            continue;   // already seen at snapshot time

                        if (TryExtractTwoFactorCode(m) is { } code)
                        {
                            Log($"Enter SMS 2FA code: {code}");
                            return code;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }

            _log.Warning($"no 2FA SMS within {timeout.TotalSeconds:F0}s — login will fail");
            return null;
        }

        /// <summary>
        /// Extracts the code from a KPN 2FA SMS, or null. Tries the decoded
        /// body first, then the raw user data as ASCII (some carrier messages
        /// keep their raw hex when undecodable).
        /// </summary>
        private static string? TryExtractTwoFactorCode(SmsMessage m)
        {
            if (!string.IsNullOrEmpty(m.Body))
            {
                var match = TwoFactorCodeRx.Match(m.Body);
                if (match.Success)
                    return match.Groups[1].Value;
            }

            if (m.UserDataHex.Length > 0 && m.UserDataHex.Length % 2 == 0)
            {
                try
                {
                    byte[] bytes = Convert.FromHexString(m.UserDataHex);
                    string ascii = new string(bytes
                        .Where(b => b is >= 0x20 and <= 0x7E)
                        .Select(b => (char)b).ToArray());
                    var match = TwoFactorCodeRx.Match(ascii);
                    if (match.Success)
                        return match.Groups[1].Value;
                }
                catch (FormatException) { /* not hex — skip */ }
            }

            return null;
        }

        // ------------------------------------------------------------------- cron

        /// <summary>
        /// Checks crontab for the watchdog line; installs it when missing (and
        /// not check-only), then re-reads crontab to verify. Returns true when
        /// the job is (now) present.
        /// </summary>
        public async Task<bool> EnsureCronJobAsync(bool checkOnly, CancellationToken ct = default)
        {
            string cronLine = _cfg["Zyxel:CronLine"] ?? DefaultCronLine;
            var shell = ConnectedShell();

            CommandResult list = await shell.RunAsync("crontab -l 2>/dev/null", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            bool present = list.Output.Contains(CronMarker, StringComparison.Ordinal);
            if (present)
            {
                if (!checkOnly)
                    Log("cron job present: " +
                        list.Output.Split('\n').First(l => l.Contains(CronMarker, StringComparison.Ordinal)).Trim());
                return true;
            }

            _log.Warning("cron job missing" + (checkOnly ? " (not installing)" : "; installing"));
            if (checkOnly)
                return false;

            string install =
                "crontab -l 2>/dev/null | grep -v " + CronMarker + " > /tmp/.cron_guard; " +
                $"echo '{cronLine}' >> /tmp/.cron_guard; " +
                "crontab /tmp/.cron_guard; rm -f /tmp/.cron_guard; " +
                "crontab -l 2>/dev/null";
            CommandResult r = await shell.RunAsync(install, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            bool ok = r.Success && r.Output.Contains(CronMarker, StringComparison.Ordinal);
            if (ok)
                Log("cron installed; crontab now: " + r.Output.Trim());
            else
                _log.Error($"cron install FAILED (exit={r.ExitCode}): {r.Output.Trim()}");
            return ok;
        }

        // -------------------------------------------------------------------- misc

        private ZyxelSettings LoadSettings()
        {
            // From the host's own configuration (appsettings.json + env vars
            // already loaded into IConfiguration), then the library's ZYXEL_*
            // environment overrides — same precedence as ZyxelSettings.Load.
            var z = _cfg.GetSection("Zyxel");
            var settings = new ZyxelSettings
            {
                Host = z["Host"] ?? "192.168.1.1",
                Username = z["Username"] ?? "admin",
                Password = z["Password"] ?? "",
                Exploit = z["Exploit"] ?? "",
                ExploitConfirm = z["ExploitConfirm"] ?? "",
                DalOid = z["DalOid"] ?? "",
                DalCommandField = z["DalCommandField"] ?? "",
                DalCommandWrap = z["DalCommandWrap"] ?? "",
            };

            if (int.TryParse(z["Port"], out int port))
                settings.SshPort = port;
            if (int.TryParse(z["RootPort"], out int rootPort))
                settings.RootPort = rootPort;
            if (z["WebUsername"] is { Length: > 0 } wu) settings.WebUsername = wu;
            if (z["WebPassword"] is { Length: > 0 } wp) settings.WebPassword = wp;

            var payloadSection = z.GetSection("DalPayload");
            if (payloadSection.GetChildren().Any())
            {
                var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (IConfigurationSection child in payloadSection.GetChildren())
                    payload[child.Key] = ConfigValueToJsonElement(child);
                settings.DalPayload = payload;
            }

            return settings.ApplyEnvironment();
        }

        /// <summary>
        /// Converts one configuration section to its JSON value: a leaf
        /// parses as JSON when possible (numbers/booleans), otherwise as a
        /// string; a section with sequential numeric keys is an array.
        /// </summary>
        private static JsonElement ConfigValueToJsonElement(IConfigurationSection section)
        {
            if (section.Value is { } value)
            {
                try { return JsonDocument.Parse(value).RootElement.Clone(); }
                catch (JsonException) { return JsonSerializer.SerializeToElement(value); }
            }

            List<IConfigurationSection> children = section.GetChildren().ToList();
            if (children.Count == 0)
                return JsonDocument.Parse("null").RootElement.Clone();

            if (children.All(c => int.TryParse(c.Key, out _)))
                return JsonSerializer.SerializeToElement(children.Select(ConfigValueToJsonElement).ToList());

            return JsonSerializer.SerializeToElement(
                children.ToDictionary(c => c.Key, ConfigValueToJsonElement));
        }

        private TimeSpan ParseDuration(string key, TimeSpan fallback)
        {
            string? text = _cfg[key];
            if (string.IsNullOrWhiteSpace(text))
                return fallback;
            return TryParseDuration(text, out TimeSpan value) ? value : fallback;
        }

        private static bool TryParseDuration(string text, out TimeSpan value)
        {
            value = default;
            if (text.Length == 0) return false;

            string number = text;
            char unit = '\0';
            if (text[^1] is 's' or 'm' or 'h')
            {
                unit = text[^1];
                number = text[..^1];
            }

            if (!double.TryParse(number, out double v) || v < 0) return false;
            value = unit switch
            {
                'h' => TimeSpan.FromHours(v),
                'm' => TimeSpan.FromMinutes(v),
                's' or _ => TimeSpan.FromSeconds(v),   // bare number = seconds
            };
            return true;
        }

        private static string FirstLine(string? text)
        {
            string t = text ?? "";
            int nl = t.IndexOf('\n');
            string line = (nl < 0 ? t : t[..nl]).Trim();
            return line.Length > 160 ? line[..160] + "…" : line;
        }

        private void Log(string message) => _log.Info(message);
    }
}
