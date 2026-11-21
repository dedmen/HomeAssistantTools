using System;
using System.Threading.Tasks;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;
using NetDaemon.HassModel.Integration;
using ZyxelRoot;

namespace HomeAssistantNetDaemon.apps.HassModel.Zyxel
{
    /// <summary>
    /// Home Assistant surface for the Zyxel FWA505. All device I/O goes through
    /// <see cref="ZyxelRouterService"/> (the ZyxelRoot root connection); this app
    /// only owns the entities and maps their state to the service's events.
    /// </summary>
    [NetDaemonApp]
    public class ZyxelHandler
    {
        private const string ConnectedEntity = "binary_sensor.zyxel_lte_connected";
        private const string EnabledEntity = "switch.zyxel_lte_enabled";
        private const string HoldEntity = "binary_sensor.zyxel_lte_hold_active";
        private const string RequestSmsButtonEntity = "button.zyxel_kpn_request_sms";
        private const string ApnReconnectButtonEntity = "button.zyxel_apn_reconnect";

        private readonly IHaContext _ha;
        private readonly IMqttEntityManager _entityManager;
        private readonly ZyxelRouterService _router;
        private readonly PrefixedLog _log;

        public ZyxelHandler(IHaContext ha, IMqttEntityManager entityManager, ZyxelRouterService router, ILogger<ZyxelHandler> logger)
        {
            _ha = ha;
            _entityManager = entityManager;
            _router = router;
            _log = new PrefixedLog("[Zyxel]", logger);

            _log.Info("startup");

            Task.Run(async () =>
            {
                await entityManager.CreateAsync(ConnectedEntity, options: new EntityCreationOptions
                {
                    DeviceClass = "connectivity",
                    Name = "LTE Connected",
                    PayloadOn = "on",
                    PayloadOff = "off"
                }).ConfigureAwait(false);

                await entityManager.CreateAsync(EnabledEntity, options: new EntityCreationOptions
                {
                    DeviceClass = "switch",
                    Name = "LTE Enabled",
                    PayloadOn = "on",
                    PayloadOff = "off"
                }).ConfigureAwait(false);

                await entityManager.CreateAsync(HoldEntity, options: new EntityCreationOptions
                {
                    Name = "LTE APN Back-off Hold",
                    PayloadOn = "on",
                    PayloadOff = "off"
                }, new
                {
                    icon = "mdi:timer-pause-outline"
                }).ConfigureAwait(false);

                // Push button (no state): one press sends the KPN data-pack
                // request SMS. HA's button default payload is "PRESS".
                await entityManager.CreateAsync(RequestSmsButtonEntity, options: new EntityCreationOptions
                {
                    Name = "KPN Request SMS"
                }, new
                {
                    icon = "mdi:message-text-send-outline"
                }).ConfigureAwait(false);

                // Push button (no state): one press soft-resets the cellular
                // module to force the APN to re-dial (radio cycle).
                await entityManager.CreateAsync(ApnReconnectButtonEntity, options: new EntityCreationOptions
                {
                    Name = "APN Reconnect"
                }, new
                {
                    icon = "mdi:refresh"
                }).ConfigureAwait(false);

                (await entityManager.PrepareCommandSubscriptionAsync(RequestSmsButtonEntity).ConfigureAwait(false))
                .Subscribe(new Action<string>(state =>
                {
                    _log.Info($"KPN request SMS button pressed ({state})");
                    _ = SendKpnRequestSmsAsync();
                }));

                (await entityManager.PrepareCommandSubscriptionAsync(ApnReconnectButtonEntity).ConfigureAwait(false))
                .Subscribe(new Action<string>(async state =>
                {
                    _log.Info($"APN reconnect button pressed ({state})");
                    try
                    {
                        await _router.ReconnectApnAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"APN reconnect failed: {ex.Message}");
                    }
                }));

                (await entityManager.PrepareCommandSubscriptionAsync(EnabledEntity).ConfigureAwait(false))
                .Subscribe(new Action<string>(async state =>
                {
                    try
                    {
                        bool enabled = state == "on";
                        await _router.SetLteEnabledAsync(enabled).ConfigureAwait(false);
                        // Confirm the applied state back to HA.
                        await _entityManager.SetStateAsync(
                            EnabledEntity, enabled ? "on" : "off").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"switch command failed: {ex.Message}");
                    }
                }));

                _router.DataStateChanged += async (_, e) =>
                {
                    try
                    {
                        await _entityManager.SetStateAsync(
                            ConnectedEntity, e.State == DataState.Connected ? "on" : "off").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"failed to set {ConnectedEntity}: {ex.Message}");
                    }
                };

                _router.HoldChanged += async (_, e) =>
                {
                    try
                    {
                        await _entityManager.SetStateAsync(
                            HoldEntity, e.Holding ? "on" : "off").ConfigureAwait(false);
                        await _entityManager.SetAttributesAsync(HoldEntity, new
                        {
                            reason = e.Reason,
                            hold_until = e.HoldUntilUtc?.ToString("o")
                        }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"failed to set {HoldEntity}: {ex.Message}");
                    }
                };

                _router.RouterRebooted += async (_, e) =>
                {
                    try
                    {
                        await _entityManager.SetAttributesAsync(ConnectedEntity, new
                        {
                            uptime_seconds = (int)e.ObservedUptimeSeconds,
                            was_down_for_seconds = (int)e.DisconnectedFor.TotalSeconds,
                            reconnects = _router.ReconnectCount
                        }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"failed to set {ConnectedEntity} attributes: {ex.Message}");
                    }
                };

                // The router may have connected (and state already changed) while
                // the entities were being created — re-seed from current values.
                await SeedStatesAsync().ConfigureAwait(false);
            });

            ha.RegisterServiceCallBack<object>("ZyxelRouterReboot", a =>
            {
                _log.Info("run router reboot");
                _ = _router.RebootAsync();
            });

            ha.RegisterServiceCallBack<object>("ZyxelSendKPNRequestSMS", a =>
            {
                _log.Info("send KPN request SMS 'NL2000 AAN' to 1266");
                _ = SendKpnRequestSmsAsync();
            });

            ha.RegisterServiceCallBack<object>("ZyxelReconnectAPN", a =>
            {
                _log.Info("run APN reconnect (radio cycle)");
                _ = _router.ReconnectApnAsync();
            });
        }

        private async Task SendKpnRequestSmsAsync()
        {
            try
            {
                ZyxelRoot.Sms.SmsSendResult r = await _router.SendSmsAsync("1266", "NL2000 AAN").ConfigureAwait(false);
                _log.Info($"KPN request SMS accepted={r.Accepted} resultCode={r.ResultCode ?? -1} outbox={r.OutboxFile}");
            }
            catch (Exception ex)
            {
                _log.Error($"KPN request SMS failed: {ex.Message}");
            }
        }

        private async Task SeedStatesAsync()
        {
            try
            {
                DataState data = _router.CurrentDataState;
                if (data != DataState.Unknown)
                    await _entityManager.SetStateAsync(
                        ConnectedEntity, data == DataState.Connected ? "on" : "off").ConfigureAwait(false);

                if (_router.IsHolding)
                {
                    await _entityManager.SetStateAsync(HoldEntity, "on").ConfigureAwait(false);
                    await _entityManager.SetAttributesAsync(HoldEntity, new
                    {
                        reason = _router.HoldReason,
                        hold_until = _router.HoldUntilUtc?.ToString("o")
                    }).ConfigureAwait(false);
                }


                // The HA switch is the source of truth for the APN: apply its
                // state to the router (the device's persistent config may have
                // drifted, e.g. changed manually on the box).
                await ApplySwitchToRouterAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The router may simply not be connected yet; the events above
                // will set the states as soon as it is.
                _log.Warning($"state seed skipped: {ex.Message}");
            }
        }

        /// <summary>
        /// Startup: reads the switch's current state from HA and applies it to
        /// the router. The switch is the source of truth — the router's
        /// persistent APN config must follow it, not the other way around.
        /// Skipped when the entity has no real state yet (fresh install).
        /// </summary>
        private async Task ApplySwitchToRouterAsync()
        {
            string? entityState;
            try
            {
                EntityState entity = _ha.GetState(EnabledEntity);
                entityState = entity.State;
            }
            catch (Exception ex)
            {
                _log.Warning($"could not read {EnabledEntity} state: {ex.Message}");
                return;
            }

            if (entityState is not ("on" or "off"))
            {
                _log.Info($"switch state '{entityState ?? "unknown"}' — nothing to apply to the router");
                return;
            }

            bool enabled = entityState == "on";
            try
            {
                // The root connection may not be up yet at startup.
                await _router.WaitForConnectionAsync().ConfigureAwait(false);
                if (enabled != await _router.ReadApnEnabledAsync().ConfigureAwait(false))
                {
                    _log.Info($"startup: applying switch state '{entityState}' to the router");
                    await _router.SetLteEnabledAsync(enabled).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.Error($"startup: applying switch state to the router failed: {ex.Message}");
            }
        }
    }
}
