using NetDaemon.Extensions.MqttEntityManager;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using System.Timers;
using Tmds.MDns;

namespace HomeAssistantNetDaemon.apps.HassModel.BatteryControl
{
    internal class BatteryInfo
    {
        public string SerialNumber;

        // curl -g -X POST -H "Content-Type: application/json" "http://10.0.2.246:8080/rpc/Indevolt.GetData?config={\"t\":[7600]}"
        public string NetworkAddress;
        public int StateOfCharge = 50; // Assume half way full, that won't cause issues whichever direction we actually end up going

        public float LastKnownState = -5000;
        // Sending a command to the battery once, sometimes it just won't obey, so we use this to repeat send the same command multiple times
        public int LastKnownStateConfirmCounter = 0;

        public readonly ConditionalTokenBucketLimiter _rateLimiter;

        public DateTimeOffset LastActive { get; internal set; } = DateTimeOffset.Now;
        public bool IsStandby { get; internal set; } = false;

        public bool IsFullyCharged { get; internal set; } = false;

        public float CurrentReportedPower { get; internal set; } = 0.0f;

        // When the battery was last _commanded_ from discharge or standby, into charge mode. Is not updated while the battery stays in charge mode
        public DateTimeOffset TimeOfLastSwitchToChargeMode { get; internal set; } = DateTimeOffset.Now;

        public int ConsequtiveRequestFailCount = 0;


        //#TODO IsFullyCharged. If we commanded it to charge at >50w for 30 seconds, and its input power is <10w, it is full.
        // Maybe by curl -g -X POST -H "Content-Type: application/json" "http://10.0.2.76:8080/rpc/Indevolt.GetData?config={\"t\":[6001]}"
        // returning {"6001":1000}, which means its in static state, despite being commanded to charge? If SoC is above 98, and we are in charge mode, ask it every 30 seconds, once it goes in static its full

        public BatteryInfo()
        {
            // Configure the rate limiter
            //_rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            //{
            //    TokenLimit = 1,                  // Maximum burst capacity
            //    QueueLimit = 0,                  // CRITICAL: 0 means drop new requests immediately if full
            //    ReplenishmentPeriod = TimeSpan.FromSeconds(4), // Refresh rate window
            //    TokensPerPeriod = 1,             // How many tokens refill per period
            //    AutoReplenishment = true
            //});
            _rateLimiter = new ConditionalTokenBucketLimiter(1, 1, TimeSpan.FromSeconds(4));
        }
    }


    internal class BatteryController : IAsyncInitializable
    {

        private List<BatteryInfo> _batteries = new();
        private ServiceBrowser _serviceBrowser;

        private HttpClient _httpClient = new();

        private System.Timers.Timer _standbyCheck = new (TimeSpan.FromSeconds(10));


        public IHaContext _ha { get; }
        public IMqttEntityManager _entityManager { get; }


        public BatteryController(IHaContext ha, IMqttEntityManager entityManager)
        {
            _ha = ha;
            _entityManager = entityManager;

            _httpClient.Timeout = TimeSpan.FromSeconds(15);

            var hostname = System.Net.Dns.GetHostName();
            if (hostname != "LAMBDA") // NetDaemon cannot reach LAN devices, but it can reach the proxy on the host
            {
                // 1. Configure the proxy pointing to your Debian host's Nginx port
                var proxy = new WebProxy
                {
                    Address = new Uri("http://10.0.0.2:8888"),
                    BypassProxyOnLocal = false
                };

                // 2. Attach the proxy configuration to the HTTP handler
                var handler = new HttpClientHandler
                {
                    Proxy = proxy,
                    UseProxy = true
                };

                _httpClient = new HttpClient(handler);
            }
            
            _serviceBrowser = new ServiceBrowser();
            _serviceBrowser.ServiceAdded += onServiceAdded;
            _serviceBrowser.ServiceRemoved += onServiceRemoved;
            _serviceBrowser.ServiceChanged += onServiceChanged;
        }


        public async Task InitializeAsync(CancellationToken cancellationToken)
        {

            // These are just for monitoring that the code is working correctly
            await _entityManager.CreateAsync("sensor.netdaemon_batterycommand_bat1", new EntityCreationOptions { Name = "Battery1 commanded Target", DeviceClass = "power", }, new
            {
                unit_of_measurement = "W",
                icon = "mdi:weather-sunset-up",
                state_class = "measurement"
            }).ConfigureAwait(false);

            await _entityManager.CreateAsync("sensor.netdaemon_batterycommand_bat2", new EntityCreationOptions { Name = "Battery2 commanded Target", DeviceClass = "power", }, new
            {
                unit_of_measurement = "W",
                icon = "mdi:weather-sunset-up",
                state_class = "measurement"
            }).ConfigureAwait(false);


            //#TODO Find all batteries automatically ?

            //foreach (var allEntity in ha.GetAllEntities())
            //{
            //    if (!allEntity.EntityId.Contains("bk1600"))
            //        continue;
            //
            //    Debugger.Break();
            //
            //    allEntity.StateChanges().Where(e => e.Entity.EntityId)
            //}

            int socState = 0;
            if (!int.TryParse(_ha.GetState("sensor.living_room_indevolt_bk1600_sec_battery_soc").State, out socState)) // state may be "unavailable"
                socState = 0;

            _batteries.Add(new BatteryInfo()
            {
                SerialNumber = _ha.GetEntityRegistration("sensor.living_room_indevolt_bk1600_sec_battery_soc").Device.SerialNumber,
                StateOfCharge = socState,
                NetworkAddress = "http://10.0.2.76:8080"
            });

            if (!int.TryParse(_ha.GetState("sensor.schlafzimmer_indevolt_bk1600_a673ba40t800780s_battery_soc").State, out socState))
                socState = 0;

            _batteries.Add(new BatteryInfo()
            {
                SerialNumber = _ha.GetEntityRegistration("sensor.schlafzimmer_indevolt_bk1600_a673ba40t800780s_battery_soc").Device.SerialNumber,
                StateOfCharge = socState,
                NetworkAddress = "http://10.0.2.246:8080"
            });

            // Monitor SoC changes
            _ha.StateChanges().Where(e =>
                e.Entity.EntityId == "sensor.schlafzimmer_indevolt_bk1600_a673ba40t800780s_battery_soc" ||
                e.Entity.EntityId == "sensor.living_room_indevolt_bk1600_sec_battery_soc"
            ).Subscribe(e =>
            {
                var serial = e.Entity.Registration.Device.SerialNumber;

                var found = _batteries.FirstOrDefault(x => x.SerialNumber == serial);

                int socState = 0;
                if (!int.TryParse(e.New.State, out socState)) // state may be "unavailable"
                    socState = 0;

                if (found != null)
                    found.StateOfCharge = socState;

#if DEBUG
                Console.WriteLine($"New SoC {socState} for {serial}");
#endif
            });

            // Monitor power level changes (We need that for fully charged detection)
            _ha.StateChanges().Where(e =>
                e.Entity.EntityId == "sensor.schlafzimmer_indevolt_bk1600_a673ba40t800780s_battery_plug_total_power" ||
                e.Entity.EntityId == "sensor.living_room_indevolt_bk1600_sec_bk1600_battery_plug_sec_total_power"
            ).Subscribe(e =>
            {
                var serial = e.Entity.Registration.Device.SerialNumber;

                var found = _batteries.FirstOrDefault(x => x.SerialNumber == serial);

                float powerLevel = 0;
                if (!float.TryParse(e.New.State, out powerLevel)) // state may be "unavailable"
                    socState = 0;

                if (found != null)
                    found.CurrentReportedPower = powerLevel;
            });



            _standbyCheck.Elapsed += _standbyCheck_Elapsed;
            _standbyCheck.Start();
        }

        private void _standbyCheck_Elapsed(object? sender, ElapsedEventArgs e)
        {
            // On interval, check if a battery has been inactive for so long, that we can send it to standby
            foreach (var item in _batteries)
            {
                if ((DateTimeOffset.Now - item.LastActive) > TimeSpan.FromMinutes(5))
                    SendStandbyCommand(item);
            }

            // Check if a battery is fulled charged

            foreach (var item in _batteries)
            {
                if (item.StateOfCharge < 99)
                    continue; // Can't be full if it doesn't report SoC at full

                if (
                    !item.IsFullyCharged && // Not yet fully charged
                    item.LastKnownState > 50 &&   // It is commanded to charge at more than 50w
                    item.CurrentReportedPower < 20 && // But the actual power it uses is below 20.
                    (DateTimeOffset.Now - item.TimeOfLastSwitchToChargeMode) > TimeSpan.FromSeconds(30) // And it had been commanded to charge for more than 30 seconds (so we don't check 1 second after mode switch, and then consider it as full because the switch has lag)
                    
                    )
                {
                    // Commanded to charge, but not charging, probably full.
                    item.IsFullyCharged = true;
                    Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Device {item.NetworkAddress} is now fully charged");
                    SendPowerCommand(item, 0); // Stop charge command, they're not taking it anyway
                }
            }


            // Lost connection because they changed IP?
            foreach (var item in _batteries)
            {
                if (item.ConsequtiveRequestFailCount > 5)
                {
                    item.ConsequtiveRequestFailCount = 0;

                    DetermineNewIPAddress(item).ContinueWith(x =>
                    {
                        if (x.Result != null)
                        {
                            item.NetworkAddress = x.Result;
                            item.ConsequtiveRequestFailCount = 0;
                        }
                    });
                }
            }
        }

        async Task<string> DetermineNewIPAddress(BatteryInfo target)
        {
            string[] test = target.SerialNumber.EndsWith("800780S") ? ["http://10.0.2.244:8080", "http://10.0.2.245:8080", "http://10.0.2.246:8080"] : ["http://10.0.2.76:8080"];


            var tasks = test.Select(x =>
            {
                return Task.Run(async () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, $"{x}");

                    try
                    {
                        var resp = await _httpClient.SendAsync(request);

                        if (resp.StatusCode == HttpStatusCode.BadGateway) // HomeAssistant runs through proxy, and this means fail
                        {
                            Console.WriteLine($"Determine Failed to send {target.NetworkAddress}: {resp.StatusCode}");
                            return null;
                        }

                        //await resp.Content.ReadAsStringAsync();
                        Console.WriteLine($"Determine success {x}: {resp.StatusCode}");
                        return x;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Determine Failed to send {target.NetworkAddress}: {ex.Message}");
                        return null;
                    }
                });
            });

            var results = await Task.WhenAll(tasks);

            return results.FirstOrDefault(x => x != null);
        }


        //#TODO handle mDNS discovery, for when a battery reconnected to wifi and changed IP
        static void onServiceChanged(object sender, ServiceAnnouncementEventArgs e)
        {
            printService('~', e.Announcement);
        }

        static void onServiceRemoved(object sender, ServiceAnnouncementEventArgs e)
        {
            printService('-', e.Announcement);
        }

        static void onServiceAdded(object sender, ServiceAnnouncementEventArgs e)
        {
            printService('+', e.Announcement);
        }
        static void printService(char startChar, ServiceAnnouncement service)
        {
            Console.WriteLine("{0} '{1}' on {2}", startChar, service.Instance, service.NetworkInterface.Name);
            Console.WriteLine("\tHost: {0} ({1})", service.Hostname, string.Join(", ", service.Addresses));
            Console.WriteLine("\tPort: {0}", service.Port);
            Console.WriteLine("\tTxt : [{0}]", string.Join(", ", service.Txt));
        }

        static readonly int MinimumSoC = 10;


        enum BatteryMode
        {
            Standby = 0,
            Charging = 1,
            Discharging = 2
        }


        void OnBatteryModeSwitched(BatteryInfo target, BatteryMode mode, float powerWatts)
        {
            if (target.LastKnownState <= 0 && powerWatts > 50) // Was discharging or standby, and switched into charging
            {
                target.TimeOfLastSwitchToChargeMode = DateTimeOffset.Now;
            }

            if (target.IsFullyCharged && mode == BatteryMode.Discharging && powerWatts < -50)
            {
                // If we commanded discharge, we have to assume its no longer fully charged
                target.IsFullyCharged = false;
                Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Device {target.NetworkAddress} is no longer fully charged");
            }

            if (target.LastKnownState == powerWatts) // Count how many times we sent the same command
                target.LastKnownStateConfirmCounter++;
            else
                target.LastKnownStateConfirmCounter = 0;

            target.LastKnownState = powerWatts;
            target.IsStandby = mode == BatteryMode.Standby;
            if (powerWatts != 0)
                target.LastActive = DateTimeOffset.Now;



            if (target == _batteries.First())
                _entityManager.SetStateAsync("sensor.netdaemon_batterycommand_bat1", $"{powerWatts}");
            else
                _entityManager.SetStateAsync("sensor.netdaemon_batterycommand_bat2", $"{powerWatts}");




        }

        // powerWatts is negative, for battery to discharge, positive for battery to charge
        void SendPowerCommand(BatteryInfo target, float powerWatts, bool forceSend = false)
        {
            // curl -g -X POST -H "Content-Type: application/json" "http://192.168.31.213:8080/rpc/Indevolt.SetData?config={\"f\":16,\"t\":47015,\"v\":[2,700,5]}"

            if (Math.Abs(powerWatts) < 50)
                powerWatts = 0;

            // We want to not repeatedly send the same state, but also the battery doesn't always obey when its only sent once, so we send the same state up to 3 times before we stop
            if (target.LastKnownState == powerWatts && target.LastKnownStateConfirmCounter > 2 && !forceSend)
                return; // No change

            // The mode's behaviors at 0 load are different. Standby does 0w (but clicks relays every time its switched to/from), charging does 30w, and discharging does 0 without clicking relays. I suspect discharge consumes power out of the batter instead
            //BatteryMode targetMode = powerWatts == 0 ? BatteryMode.Standby : (powerWatts > 0 ? BatteryMode.Charging : BatteryMode.Discharging); // 0=standby, 1=charging, 2=discharging
            BatteryMode targetMode = powerWatts == 0 ? BatteryMode.Discharging : (powerWatts > 0 ? BatteryMode.Charging : BatteryMode.Discharging); // 0=standby, 1=charging, 2=discharging


            var postContent = new
            {
                f = 16,
                t = 47015,
                v = new int[]
                {
                    (int)targetMode,
                    (int) Math.Round(Math.Abs(powerWatts)),
                    powerWatts > 0 ? 100 : MinimumSoC // Charge to 100%, discharge to 20%
                }
            };

            Task.Run(async () =>
            {
                if (forceSend) // Actually wait, for a lease to become available
                {
                    using RateLimitLease lease = await target._rateLimiter.AcquireAsync(1);

                    if (lease.IsAcquired) // We may have been dropped, because another force send won
                    {
                        if (target.LastKnownState == powerWatts) // While we were waiting, it already got to where we wanted it?
                            return;

                        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Forced power command {target.NetworkAddress} from {target.LastKnownState} to {powerWatts}");
                        SendPowerCommandInternal(target, powerWatts, targetMode, postContent);
                    }
                }
                else // Try to send if rate limiter allows, if not then just drop it
                {
                    using RateLimitLease lease = target._rateLimiter.AttemptAcquire(1);

                    if (lease.IsAcquired)
                    {
                        SendPowerCommandInternal(target, powerWatts, targetMode, postContent);
                    }
                    else
                    {
#if DEBUG
                        Console.WriteLine($"[DROPPED] Set {target.NetworkAddress} to {powerWatts}");
#endif
                    }
                }


            });
        }

        private async void SendPowerCommandInternal(BatteryInfo target, float powerWatts, BatteryMode targetMode, object postContent)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{target.NetworkAddress}/rpc/Indevolt.SetData?config={JsonSerializer.Serialize(postContent)}");

#if DEBUG
            Console.WriteLine($"Set {target.NetworkAddress} to {powerWatts}");
#endif
            try
            {
#if DEBUG
                Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Send... {target.NetworkAddress}");
#endif
                var resp = await _httpClient.SendAsync(request);
                //await resp.Content.ReadAsStringAsync();
#if DEBUG
                Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} Sent {target.NetworkAddress} {resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
#endif
                if (resp.StatusCode == HttpStatusCode.BadGateway) // In HomeAssistant this runs through a proxy, that will not fail, but report a BadGateway
                {
                    Console.WriteLine($"Failed to send {target.NetworkAddress}: {resp.StatusCode}");
                    target.ConsequtiveRequestFailCount++;
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to send {target.NetworkAddress}: {ex.Message}");
                target.ConsequtiveRequestFailCount++;
                return;
            }

            // Successfully changed it
            OnBatteryModeSwitched(target, targetMode, powerWatts);
            target.ConsequtiveRequestFailCount = 0;
        }

        void SendStandbyCommand(BatteryInfo target)
        {
            if (target.IsStandby)
                return;

            var postContent = new
            {
                f = 16,
                t = 47015,
                v = new int[]
                {
                    0, // 0=standby, 1=charging, 2=discharging
                    (int) 0,
                    100
                }
            };

            Task.Run(async () =>
            {
                using RateLimitLease lease = await target._rateLimiter.AcquireAsync(1);

//#if DEBUG
                Console.WriteLine($"{DateTimeOffset.Now: HH:mm:ss.fff} Set {target.NetworkAddress} to STANDBY");
//#endif

                SendPowerCommandInternal(target, 0, BatteryMode.Standby, postContent);
            });
        }


        List<(BatteryInfo, float)> DistributePower(int powerWatts)
        {
            var batteries = new List<BatteryInfo>();
            batteries.AddRange(_batteries);

            List<(BatteryInfo, float)> result = new();

            while (Math.Abs(powerWatts) > 10 && batteries.Any())
            {
                // Charge
                if (powerWatts > 0)
                {
                    //if (powerWatts < 100) // Minimum charge power is 100w, if we can't manage it, just standby the battery
                    //    break;

                    batteries = batteries.Where(x => !x.IsFullyCharged).ToList(); // Exclude fully charged batteries, they don't need any extra charging
                    if (!batteries.Any())
                        break; // All full, leave the while loop, which will cause all batteries to be set to zero

                    // If charging, first put all into lowest SoC battery, until ít comes within 5% of others. then divide it over both (Needs at least 100w charge power, otherwise we can only do one at a time
                    // Note we tell even full batteries to charge, which is fine. Sadly 100% SoC doesn't mean its full, so we cannot use that. We will just keep commanding them to charge and they just won't.

                    // If lowest battery is more than 5% away from highest one, put all into that one first

                    if (batteries.Min(x => x.StateOfCharge) < batteries.Max(x => x.StateOfCharge) - 5)
                    {
                        var batteryToUse = batteries.MinBy(x => x.StateOfCharge)!;

                        //Console.WriteLine($"BAT charging lowest {powerWatts}");
                        // The min is lowest, give it all the charge

                        //#TODO if we charge more than 1200W, we need to split it to second lowest anyway
                        
                        result.Add((batteryToUse, powerWatts));
                        powerWatts -= powerWatts;
                        batteries.Remove(batteryToUse);
                        break;
                    }

                    // Batteries are close together, if we don't have enough power, we just give it to first
                    // We need a bit of space here to decide when to switch from one battery to multiple, or from multiple to one
                    // Otherwise if we are around the limit, we end up flipping back and forth
					
                    bool multiBatteryMode = false;
                    var countActiveBatteries = batteries.Count(x => x.LastKnownState > 50); // Batteries that do more than 50w (which is the minimum) charge

                    if (countActiveBatteries > 1)
                    {
                        multiBatteryMode = true; // Already both batteries active, we can stay on both active.
                        // Unless load has gone so low that we really don't need anymore

                        if (powerWatts < 200) // Less than 200, too low to supply both at once
                            multiBatteryMode = false;
                    }
                    else
                    {
                        // Not yet two batteries active, we activate them once we have enough power to supply both, and likely won't drop below that again soon
                        if (powerWatts > 400)
                            multiBatteryMode = true;
                    }

                    if (multiBatteryMode)
                    {
                        //Console.WriteLine($"BAT scharge split {powerWatts / batteries.Count}");

                        while (batteries.Any())
                        {
                            result.Add((batteries.First(), powerWatts / batteries.Count));
                            powerWatts -= powerWatts / batteries.Count;
                            batteries.Remove(batteries.First());
                        }

                        break;
                    }

                    {
                        // Used to give the power to the lowest battery, but this caused two batteries to constantly switch places, with every percent change of SoC, because the other one would become lower
                        // Instead of choosing the lowest, prefer a battery that is already charging. That way the 5% SoC delta code above, will take care of switching between the batteries, and it won't flip around that often.

                        // The one that is already charging
                        var batteryToUse = batteries.FirstOrDefault(x => x.LastKnownState > 50);

                        // Otherweise give all to lowest
                        if (batteryToUse == null)
                            batteryToUse = batteries.MinBy(x => x.StateOfCharge)!;

                        //Console.WriteLine($"BAT charge all to lowest {powerWatts}");

                        result.Add((batteryToUse, powerWatts));
                        powerWatts -= powerWatts;
                        batteries.Remove(batteryToUse);
                        break;
                    }
                }

                // Discharge
                if (powerWatts < 0) 
                {
                    batteries = batteries.Where(x => x.StateOfCharge > MinimumSoC).ToList(); // Exclude empty batteries, they can't provide power
                    if (!batteries.Any())
                        break; // All empty, leave the while loop, which will cause all batteries to be set to zero

                    // Almost just the opposite logic, but, if we take low power loads from the lowest SoC device, we would constantly alternate between the batteries. So low power discharge just uses the first battery, until empty.

                    // We need a bit of space here to decide when to switch from one battery to multiple, or from multiple to one
                    // Otherwise if we are around the limit, we end up flipping back and forth

                    bool multiBatteryMode = false;
                    var countActiveBatteries = batteries.Count(x => x.LastKnownState < -50); // Batteries that do more than 50w (which is the minimum) discharge

                    if (countActiveBatteries > 1)
                    {
                        multiBatteryMode = true; // Already both batteries active, we can stay on both active.
                        // Unless load has gone so low that we really don't need anymore

                        if (powerWatts > -200) // Less than 200 required, just one battery is enough
                            multiBatteryMode = false;
                    }
                    else
                    {
                        // Not yet two batteries active, we activate them if there is enough work for them
                        var threshold = -400;

                        // If the inactive battery is in standby, avoid waking it up
                        if (batteries.Where(x => x.LastKnownState == 0).Count(x => x.IsStandby) > 0)
                            threshold = -600;

                        if (powerWatts < threshold)
                            multiBatteryMode = true;
                    }

                    if (multiBatteryMode)
                    {
                        // Big power discharge, distribute across all batteries
                        //Console.WriteLine($"BAT discharge split {powerWatts / batteries.Count}");

                        while (batteries.Any())
                        {
                            result.Add((batteries.First(), powerWatts / batteries.Count));
                            powerWatts -= powerWatts / batteries.Count;
                            batteries.Remove(batteries.First());
                        }

                        break;
                    }

                    {
                        // Small load, just take first usable battery 
                        var batteryToUse = batteries.First();

                        //Console.WriteLine($"BAT discharge first {powerWatts}");

                        result.Add((batteryToUse, powerWatts));
                        powerWatts -= powerWatts;
                        batteries.Remove(batteryToUse);
                        break;
                    }
                }
            }

            // Set the remaining batteries, to zero
            foreach (var item in _batteries.Where(x => !result.Any(y => y.Item1 == x)))
            {
                result.Add((item, 0));
            }

            return result;
        }

        // powerWatts is negative, for battery to discharge, positive for battery to charge
        public void SetBatteryPower(float powerWatts, bool forceSend = false)
        {
            // Enforce the batteries limits here

            // We cannot do less than 50w discharge, the batteries would just go to zero
            if (powerWatts < 0 && powerWatts > -50)
                powerWatts = -50;

            // The minimum charge we can do is 100w (actually 120w on plug), we never want to overcharge
            //if (powerWatts > 0 && powerWatts < 110)
            //    powerWatts = 0; // Standby the battery instead


            //Console.WriteLine($"Battery command to {powerWatts}, force {forceSend}");
            // We need to choose which batteries to task

            var split = DistributePower((int)powerWatts);

            foreach (var item in split)
            {
                SendPowerCommand(item.Item1, item.Item2, forceSend);
            }
        }
    }
}
