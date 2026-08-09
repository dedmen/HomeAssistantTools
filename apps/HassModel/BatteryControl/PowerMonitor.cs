using Microsoft.Extensions.Configuration;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.HassModel.Entities;
using Org.BouncyCastle.Asn1.X509;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace HomeAssistantNetDaemon.apps.HassModel.BatteryControl
{
    [NetDaemonApp]
    [Focus]
    public class PowerMonitor : IAsyncInitializable
    {
        private readonly IHaContext _ha;
        private readonly IConfiguration _cfg;
        private readonly IMqttEntityManager _entityManager;

        private BatteryController _batteryController;


        // At this many watts off the target, we ignore all filters and rate limiting and try to push for instant correction
        // For Oven/Microwave turning on, or a sudden solar dip
        private static readonly float InstantCorrectionThreshold = 100; 

        AdaptivePowerFilter _filter = new AdaptivePowerFilter(0, 150);
        
        public readonly TokenBucketRateLimiter _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,                  // Maximum burst capacity
            QueueLimit = 1,                  // CRITICAL: 0 means drop new requests immediately if full
            ReplenishmentPeriod = TimeSpan.FromSeconds(2), // Refresh rate window
            TokensPerPeriod = 1,             // How many tokens refill per period
            AutoReplenishment = true
        });

        // Apply small power changes slowly, don't annoy the inverter for small adjustments. Large changes bypass the limiter
        public readonly DynamicMagnitudeRateLimiter _rateLimiterApply = new DynamicMagnitudeRateLimiter((int)InstantCorrectionThreshold, TimeSpan.FromSeconds(30));


        public class PowerEntity
        {
            public float PowerAtLastMeter;
            public float PowerLiveData;

            public enum PowerType
            {
                Grid,
                Battery,
                Load,
                Solar
            }

            public PowerType Type;

            public float GetDeltaSinceLastMeter()
            {
                return PowerLiveData - PowerAtLastMeter;
            }
        }

        private readonly Dictionary<string, PowerEntity> _powerMeters = new();

        private readonly (string, PowerEntity.PowerType)[] _powerMetersNames =
        [
            ("sensor.solar_power", PowerEntity.PowerType.Solar),
            ("sensor.energy_grid_nrg_dongle_pro_power_delivered_nrg_dongle_pro_power_returned_net_power", PowerEntity.PowerType.Grid),
            ("sensor.schlafzimmer_indevolt_bk1600_a673ba40t800780s_battery_plug_total_power", PowerEntity.PowerType.Battery),
            ("sensor.living_room_indevolt_bk1600_sec_bk1600_battery_plug_sec_total_power", PowerEntity.PowerType.Battery),
            ("sensor.main_pc_power", PowerEntity.PowerType.Load),
            ("sensor.oven_power_2", PowerEntity.PowerType.Load)
        ];

        private ShellyEmulator _shellyEmulator;

        public PowerMonitor(IHaContext ha, IConfiguration cfg, IMqttEntityManager entityManager)
        {
            _ha = ha;
            _cfg = cfg;
            _entityManager = entityManager;

            _shellyEmulator = new ShellyEmulator();
            _batteryController = new BatteryController(ha);
        }


        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await _entityManager.CreateAsync("sensor.netdaemon_targetpower", new EntityCreationOptions { Name = "Power Monitor Wanted Target", DeviceClass = "power", }, new
            {
                unit_of_measurement = "W",
                icon = "mdi:weather-sunset-up",
                state_class = "measurement"
            }).ConfigureAwait(false);

            await _entityManager.CreateAsync("sensor.netdaemon_batterycommand", new EntityCreationOptions { Name = "Battery commanded Target", DeviceClass = "power", }, new
            {
                unit_of_measurement = "W",
                icon = "mdi:weather-sunset-up",
                state_class = "measurement"
            }).ConfigureAwait(false);

            // Energy devices

            // Register them all
            foreach (var name in _powerMetersNames)
            {
                var state = _ha.GetState(name.Item1)?.State;
                if (state == null)
                    continue; // Missing device? //#TODO log message, or notification

                float.TryParse(state, out var currentState); // If it fails its zero, which is fine

                _powerMeters.Add(name.Item1, new PowerEntity() { PowerAtLastMeter = currentState, PowerLiveData = currentState, Type = name.Item2 });
            }

            // Subscribe to their state updates
            _ha.StateChanges().Where(e => _powerMeters.ContainsKey(e.Entity.EntityId)).Subscribe(e =>
            {
                if (e.New?.State == null)
                    return;

                //Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {e.Entity.EntityId}: {e.New.LastUpdated:HH:mm:ss.fff} / {e.New.State}");

                var updatedEntity = _powerMeters[e.Entity.EntityId];
                var newPowerState = float.Parse(e.New.State, CultureInfo.InvariantCulture);

                if (updatedEntity.Type == PowerEntity.PowerType.Grid)
                {
                    // There is a bug where meter shows wrong direction. If the fridge turns on and our power usage suddenly increases, the meter actually reports a surge in power export, wrong direction
                    // To fix this issue, we ignore one meter reading, if its a negative spike
                    // A sudden export spike is either a false positive, or a solar spike. We can deal with missing 10 seconds of solar, but we don't want to switch to charge mode, in the middle of night when the fridge turned on

                    // We were already in discharge
                    // A greater than 100w jump, into more grid export
                    if (updatedEntity.PowerAtLastMeter < 0 && (newPowerState - updatedEntity.PowerAtLastMeter) < -100)
                    {
                        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Sudden grid meter spike from {updatedEntity.PowerAtLastMeter} to {newPowerState}, ignoring spike");
                        // We need to update the PowerAtLastMeter though, so that if the next reading still reports same, we don't fall into here again
                        updatedEntity.PowerAtLastMeter = newPowerState;

                        return; // Ignore this update
                    }
                }

                updatedEntity.PowerLiveData = newPowerState;

                if (updatedEntity.Type == PowerEntity.PowerType.Grid)
                {
                    // Everyone's live data is now synced up with the meter data
                    foreach (var item in _powerMeters)
                    {
                        item.Value.PowerAtLastMeter = item.Value.PowerLiveData;
                    }

                    OnUpdate(); // Meter updates, have actual live total consumption data, they are the most accurate data we get, they can bypass the rate limit
                }

                Task.Run(async () =>
                {
                    using RateLimitLease lease = await _rateLimiter.AcquireAsync(1);
                    lock (_rateLimiter)
                    {
                        OnUpdate();
                    }
                });
            });

        }


        private void OnUpdate()
        {
            // Meter updates every 10 seconds
            var lastMeter = _powerMeters.First(x => x.Value.Type == PowerEntity.PowerType.Grid).Value.PowerLiveData;

            // To that we add the delta's that our fast updating data sources provide.
            var totalDeltaSinceLastMeter = _powerMeters.Select(x =>
            {
                if (x.Value.Type == PowerEntity.PowerType.Solar) // Solar is inverted, positive value means we are getting power, not consuming
                    return -x.Value.GetDeltaSinceLastMeter();
                if (x.Value.Type == PowerEntity.PowerType.Grid) // Grid meter is always current, except when we ignored a reading, which we did intentionally and don't want to respect it here
                    return 0;
                return x.Value.GetDeltaSinceLastMeter();
            }).Sum();


            var meterTarget = -50; // This is the watts we want to see on the meter. Slight export

            var currentMeter = lastMeter + totalDeltaSinceLastMeter;

            var deltaToTarget = meterTarget - currentMeter;

            var currentBatteryLoad = _powerMeters.Where(x => x.Value.Type == PowerEntity.PowerType.Battery).Select(x => x.Value.PowerLiveData).Sum();

            // Give the batteries a new target, such that we get to our meterTarget
#if DEBUG
            Console.WriteLine($"{DateTimeOffset.Now: HH:mm:ss.fff} currentMeter {currentMeter} ({lastMeter} + {totalDeltaSinceLastMeter})");
            Console.WriteLine($"{DateTimeOffset.Now: HH:mm:ss.fff} deltaToTarget {deltaToTarget}");
#endif

            {
                using var rateLimitTicket = _rateLimiterApply.AttemptAcquire((int)Math.Abs(deltaToTarget));

#if DEBUG
                Console.WriteLine($"{DateTimeOffset.Now: HH:mm:ss.fff} Battery to {currentBatteryLoad + deltaToTarget} ({currentBatteryLoad} + {deltaToTarget}), Rate Limit {rateLimitTicket.IsAcquired}");
#endif

                _entityManager.SetStateAsync("sensor.netdaemon_targetpower", $"{currentBatteryLoad + deltaToTarget}");

                if (rateLimitTicket.IsAcquired)
                {
                    if (Math.Abs(deltaToTarget) > InstantCorrectionThreshold)
                    {
                        _batteryController.SetBatteryPower(currentBatteryLoad + deltaToTarget, true);
                        _entityManager.SetStateAsync("sensor.netdaemon_batterycommand", $"{currentBatteryLoad + deltaToTarget}");
                    }
                    else
                    {
                        var commandPower = _filter.Filter(currentBatteryLoad + deltaToTarget);
                        _batteryController.SetBatteryPower(commandPower);
                        _entityManager.SetStateAsync("sensor.netdaemon_batterycommand", $"{commandPower}");
                    }

                    
                }
            }

            _shellyEmulator.SetPowerPhase(0, -_filter.Filter(deltaToTarget));
            _shellyEmulator.SetPowerPhase(1, -deltaToTarget);
            _shellyEmulator.SetPowerPhase(2, currentMeter);


            _shellyEmulator.SetDebugData(new
            {
                lastMeter,
                totalDeltaSinceLastMeter,
                currentMeter,
                deltaToTarget,
                currentBatteryLoad
            });
        }


    }
}
