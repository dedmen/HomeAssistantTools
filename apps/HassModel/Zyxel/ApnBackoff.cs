using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZyxelRoot;

namespace HomeAssistantNetDaemon.apps.HassModel.Zyxel
{
    /// <summary>Raised by <see cref="ApnBackoff"/> when the hold state changes
    /// (start, extend, release, reboot re-arm).</summary>
    public sealed class HoldStateEventArgs : EventArgs
    {
        /// <summary>True while the APN is being held (cm is not (re-)dialing).</summary>
        public required bool Holding { get; init; }

        /// <summary>Why the hold is active (kick, LTE switch off, re-arm), or null when released.</summary>
        public string? Reason { get; init; }

        /// <summary>When the hold is scheduled to release, or null for an
        /// indefinite (manual) hold, or when released.</summary>
        public DateTime? HoldUntilUtc { get; init; }
    }

    /// <summary>
    /// The APN back-off state machine: on a kick, <c>cmctl send OnHold</c> stops
    /// cm from (re-)dialing so the other device on the same account keeps the
    /// connection; after the back-off, <c>cmctl send GoOn</c> lets cm re-dial.
    /// </summary>
    /// <remarks>
    /// Ported from <c>ZyxelRoot.Sample.Full</c> with two additions for the
    /// Home Assistant integration:
    /// <list type="bullet">
    /// <item>Manual holds: <see cref="HoldAsync"/> with <c>duration == null</c>
    /// holds indefinitely (released by <see cref="ReleaseAsync"/>) — used by
    /// <c>switch.zyxel_lte_enabled</c> when it is turned off.</item>
    /// <item>The <see cref="HoldChanged"/> event, which
    /// <c>binary_sensor.zyxel_lte_hold_active</c> binds to.</item>
    /// </list>
    /// A kick while already holding extends the hold to the full duration. A
    /// router reboot resets cm's hold state, so <see cref="OnRouterRebootedAsync"/>
    /// re-arms <c>OnHold</c> when the back-off is still active. A kick while a
    /// manual (indefinite) hold is active is ignored — the manual hold wins.
    /// </remarks>
    public sealed class ApnBackoff
    {
        private static readonly TimeSpan CmdTimeout = TimeSpan.FromSeconds(15);

        private readonly PersistentRootShell _shell;
        private readonly TimeSpan _duration;
        private readonly Action<string> _log;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private DateTime _holdUntilUtc = DateTime.MinValue;
        private CancellationTokenSource? _timerToken;
        /// <summary>
        /// Device-side truth: OnHold was sent and no GoOn has been sent since.
        /// Kept separate from <see cref="IsHolding"/> so a process exit always
        /// releases a hold the device still has (even if the local timer misfired).
        /// </summary>
        private bool _deviceHolding;
        private string? _reason;

        // ---------------------------------------------------------------- kick trigger
        // The vendor prints the "APN disconnected / Reset PDN" teardown lines on EVERY
        // (re)connect — including the box's own GoOn→redial — so a single line is NOT
        // proof of a kick. A real same-account fight shows repeated teardowns; a
        // reconnect or one-off hiccup shows at most one. So: require KickThreshold
        // distinct episodes within KickWindow, and ignore the burst right after our
        // own GoOn (ReconnectGrace).
        public int KickThreshold { get; set; } = 3;
        public TimeSpan KickWindow { get; set; } = TimeSpan.FromSeconds(120);
        public TimeSpan EventDedupe { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan ReconnectGrace { get; set; } = TimeSpan.FromSeconds(15);

        private readonly List<DateTime> _teardownEvents = new();
        private DateTime _lastTeardownEventUtc = DateTime.MinValue;
        private DateTime _reconnectGraceUntilUtc = DateTime.MinValue;

        public ApnBackoff(PersistentRootShell shell, TimeSpan duration, Action<string> log)
        {
            _shell = shell ?? throw new ArgumentNullException(nameof(shell));
            if (duration < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));
            _duration = duration;
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>Fired when the hold state changes. Raised while the gate is
        /// held — subscribers must not call back into this instance.</summary>
        public event EventHandler<HoldStateEventArgs>? HoldChanged;

        /// <summary>True while the back-off is active (kick-driven or manual).</summary>
        public bool IsHolding => DateTime.UtcNow < _holdUntilUtc;

        /// <summary>True while the device still has OnHold armed (needs a GoOn).</summary>
        public bool DeviceHolding => _deviceHolding;

        /// <summary>Why the current hold is active (kick, switch, re-arm), or null.</summary>
        public string? Reason => _reason;

        /// <summary>When the hold is scheduled to release; null for an indefinite
        /// (manual) hold or when not holding.</summary>
        public DateTime? HoldUntilUtc =>
            _holdUntilUtc == DateTime.MaxValue || !IsHolding ? null : _holdUntilUtc;

        /// <summary>
        /// Startup safety: always send GoOn. A previous (crashed) run may have
        /// left the device in <c>OnHold</c> state; GoOn clears that. GoOn on a box
        /// that is not held is a verified no-op, so no hold-state detection is
        /// needed.
        /// </summary>
        public async Task StartupGoOnAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _log("startup safety: sending GoOn (releases a stale hold from a previous run; no-op when not held)");
                await SendGoOnAsync("startup safety").ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Immediate hold, bypassing the debounce (kick self-test, or the LTE
        /// switch being turned off). <paramref name="duration"/> of null holds
        /// indefinitely — released only by <see cref="ReleaseAsync"/>.
        /// </summary>
        public async Task HoldAsync(string reason, TimeSpan? duration = null, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await StartHoldAsync(reason, duration).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Releases the hold (sends GoOn, clears the timer and deadline). No-op
        /// when nothing is held locally and the device is not holding either.
        /// </summary>
        public async Task ReleaseAsync(string reason, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!IsHolding && !_deviceHolding)
                {
                    _log($"release ({reason}): not holding — nothing to do");
                    return;
                }

                _holdUntilUtc = DateTime.MinValue;
                _reason = null;
                CancelTimer();
                await SendGoOnAsync(reason).ConfigureAwait(false);
                RaiseHoldChanged();
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Immediate hold with the configured duration, bypassing the debounce —
        /// used by the kick self-test. Live mmm.log teardown lines must go
        /// through <see cref="OnTeardownSignatureAsync"/> instead.
        /// </summary>
        public async Task OnKickDetectedAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await StartHoldAsync("kick detected", _duration).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Live entry point for one mmm.log teardown-signature line. Distinct
        /// episodes (bursts within <see cref="EventDedupe"/> collapse to one)
        /// are counted, and the hold starts only once <see cref="KickThreshold"/>
        /// land within <see cref="KickWindow"/>. Lines inside the post-GoOn
        /// <see cref="ReconnectGrace"/> are ignored (they are our own reconnect,
        /// not a kick). A manual (indefinite) hold suppresses the kick trigger.
        /// </summary>
        public async Task OnTeardownSignatureAsync(string line, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                DateTime now = DateTime.UtcNow;

                if (now < _reconnectGraceUntilUtc)
                {
                    _log($"teardown line in post-GoOn grace (own reconnect) — ignored: {First(line)}");
                    return;
                }

                if (now - _lastTeardownEventUtc < EventDedupe)
                    return;   // same teardown episode, already counted

                _lastTeardownEventUtc = now;
                _teardownEvents.Add(now);
                while (_teardownEvents.Count > 0 && now - _teardownEvents[0] > KickWindow)
                    _teardownEvents.RemoveAt(0);

                if (_teardownEvents.Count < KickThreshold)
                {
                    _log($"teardown episode {_teardownEvents.Count}/{KickThreshold} within {KickWindow.TotalSeconds:F0}s — watching");
                    return;
                }

                _teardownEvents.Clear();

                if (_holdUntilUtc == DateTime.MaxValue)
                {
                    _log("kick signature met while a manual (indefinite) hold is active — ignored");
                    return;
                }

                await StartHoldAsync("kick threshold met", _duration).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task StartHoldAsync(string reason, TimeSpan? duration)
        {
            bool wasHolding = IsHolding;
            bool wasIndefinite = _holdUntilUtc == DateTime.MaxValue;
            _holdUntilUtc = duration is null ? DateTime.MaxValue : DateTime.UtcNow.Add(duration.Value);
            _reason = reason;

            if (wasHolding && wasIndefinite)
            {
                _log($"kick during manual hold — staying held until released ({reason})");
            }
            else if (wasHolding)
            {
                _log($"kick again during hold — extending to {(duration is null ? "manual" : _holdUntilUtc.ToLocalTime().ToString("HH:mm:ss"))}");
            }
            else if (_deviceHolding)
            {
                _log("device already holding (stale) — tracking without re-sending OnHold");
            }
            else
            {
                await SendHoldAsync(reason).ConfigureAwait(false);
            }

            RestartTimer();
            RaiseHoldChanged();
        }

        /// <summary>
        /// Rebooted while holding: cm restarted and lost its hold state, so the
        /// box would re-dial (and win the account back) immediately — re-arm OnHold.
        /// The PC-side timer keeps running either way.
        /// </summary>
        public async Task OnRouterRebootedAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (IsHolding)
                {
                    _log($"reboot during active hold ({_reason ?? "?"}) — cm state reset, re-arming OnHold");
                    await SendHoldAsync("re-arm after reboot").ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Process exit: release a hold the DEVICE still has unless
        /// <paramref name="keepHold"/>. Judged on <see cref="DeviceHolding"/>
        /// (the last OnHold/GoOn actually sent), not the local timer, so a
        /// misfired timer can never leave the box silently held.
        /// </summary>
        public async Task ShutdownAsync(bool keepHold, CancellationToken ct = default)
        {
            if (!DeviceHolding) return;

            if (keepHold)
            {
                _log($"leaving OnHold active (manual 'cmctl send GoOn' to release)");
                return;
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _holdUntilUtc = DateTime.MinValue;
                _reason = null;
                CancelTimer();
                await SendGoOnAsync("process exit").ConfigureAwait(false);
                RaiseHoldChanged();
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task SendHoldAsync(string reason)
        {
            CommandResult r = await _shell.RunAsync("cmctl send OnHold", CmdTimeout, CancellationToken.None).ConfigureAwait(false);
            _deviceHolding = true;
            string reply = First(r.Output);
            string until = _holdUntilUtc == DateTime.MaxValue
                ? "until released"
                : $"re-enable at {_holdUntilUtc.ToLocalTime():HH:mm:ss}";
            _log($"APN hold STARTED ({reason}): exit={r.ExitCode} {reply} — {until}");
        }

        private async Task SendGoOnAsync(string reason)
        {
            CommandResult r = await _shell.RunAsync("cmctl send GoOn", CmdTimeout, CancellationToken.None).ConfigureAwait(false);
            _deviceHolding = false;
            CommandResult v = await _shell.RunAsync(
                "cmctl get apnInfo 1 | grep -a -E '^(apnConnStatus|ifName)'", CmdTimeout, CancellationToken.None).ConfigureAwait(false);
            _log($"APN hold released ({reason}): GoOn exit={r.ExitCode}; {First(r.Output)}; now: {First(v.Output)}");

            // The redial this GoOn triggers prints the SAME teardown lines on
            // reconnect (it re-triggered a "kick" instantly in live testing).
            // Ignore teardown lines for the grace window so our own reconnect is
            // never read as a new kick.
            _reconnectGraceUntilUtc = DateTime.UtcNow + ReconnectGrace;
        }

        private void RestartTimer()
        {
            CancelTimer();

            if (_holdUntilUtc == DateTime.MaxValue)
                return;   // manual hold: released only by ReleaseAsync

            // Capture the deadline THIS timer is accountable for. On fire we
            // compare it against the current _holdUntilUtc: if a newer kick moved
            // the deadline, a newer timer owns the release and we back off.
            DateTime deadlineUtc = _holdUntilUtc;
            double ms = Math.Max(0, (deadlineUtc - DateTime.UtcNow).TotalMilliseconds);
            _timerToken = new CancellationTokenSource();
            var token = _timerToken;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(ms), token.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;   // hold extended or shutdown
                }

                try
                {
                    await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        if (deadlineUtc != _holdUntilUtc) return;   // extended; newer timer releases
                        _holdUntilUtc = DateTime.MinValue;
                        _reason = null;
                        await SendGoOnAsync("back-off elapsed").ConfigureAwait(false);
                        RaiseHoldChanged();
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
                catch (Exception ex)
                {
                    _log($"back-off release FAILED: {ex.Message} (send 'cmctl send GoOn' on the router)");
                }
            }, CancellationToken.None);
        }

        private void CancelTimer()
        {
            try { _timerToken?.Cancel(); } catch (ObjectDisposedException) { }
            _timerToken?.Dispose();
            _timerToken = null;
        }

        private void RaiseHoldChanged()
        {
            HoldChanged?.Invoke(this, new HoldStateEventArgs
            {
                Holding = IsHolding,
                Reason = _reason,
                HoldUntilUtc = HoldUntilUtc
            });
        }

        private static string First(string? text)
        {
            string t = (text ?? "").Replace("\r", " ").Replace("\n", " | ").Trim();
            return t.Length > 120 ? t[..120] + "…" : t;
        }
    }
}
