using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HomeAssistantNetDaemon.apps.HassModel.BatteryControl
{
    internal class ShellyEmulator
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Dictionary<string, Func<HttpListenerContext, Task>> _routes;



        //{
        //    "energyPhase0":{"totalConsumption":24.0,"totalProduction":0.0},
        //    "energyPhase1":{"totalConsumption":24.0,"totalProduction":0.0},
        //    "energyPhase2":{"totalConsumption":24.0,"totalProduction":0.0},
        //    "powerPhase0":{"apparentPower":24.0,"current":0.1,"frequency":50.0,"power":24.0,"powerFactor":1.0,"voltage":230.0},
        //    "powerPhase1":{"apparentPower":24.0,"current":0.1,"frequency":50.0,"power":24.0,"powerFactor":1.0,"voltage":230.0},
        //    "powerPhase2":{"apparentPower":24.0,"current":0.1,"frequency":50.0,"power":24.0,"powerFactor":1.0,"voltage":230.0},
        //    "usageConstraint":"NONE"
        //}



        public struct MeterReading
        {
            public MeterReading(float gridp1, float gridp2, float gridp3)
            {
                energyPhase0 = new MeterReading.EnergyPhase
                {
                    totalConsumption = gridp1 > 0 ? gridp1 : 0,
                    totalProduction = gridp1 < 0 ? -gridp1 : 0
                };
                powerPhase0 = new MeterReading.PowerPhase
                {
                    apparentPower = gridp1,
                    current = gridp1 / 230.0f,
                    power = gridp1
                };

                energyPhase1 = new MeterReading.EnergyPhase
                {
                    totalConsumption = gridp2 > 0 ? gridp2 : 0,
                    totalProduction = gridp2 < 0 ? -gridp2 : 0
                };
                powerPhase1 = new MeterReading.PowerPhase
                {
                    apparentPower = gridp2,
                    current = gridp2 / 230.0f,
                    power = gridp2
                };

                energyPhase2 = new MeterReading.EnergyPhase
                {
                    totalConsumption = gridp3 > 0 ? gridp3 : 0,
                    totalProduction = gridp3 < 0 ? -gridp3 : 0
                };
                powerPhase2 = new MeterReading.PowerPhase
                {
                    apparentPower = gridp3,
                    current = gridp3 / 230.0f,
                    power = gridp3
                };
            }

            public struct EnergyPhase
            {
                public float totalConsumption { get; set; }
                public float totalProduction { get; set; }
            }

            public struct PowerPhase
            {
                public PowerPhase()
                {
                }

                public float apparentPower { get; set; }
                public float current { get; set; }
                public float frequency { get; set; } = 50;
                public float power { get; set; }
                public float powerFactor { get; set; } = 1;
                public float voltage { get; set; } = 230;
            };

            public EnergyPhase energyPhase0 { get; set; }
            public PowerPhase powerPhase0 { get; set; }
            public EnergyPhase energyPhase1 { get; set; }
            public PowerPhase powerPhase1 { get; set; }

            public EnergyPhase energyPhase2 { get; set; }
            public PowerPhase powerPhase2 { get; set; }
            public string usageConstraint { get; set; } = "NONE";
        }

        public struct ShellyStatus
        {
            public ShellyStatus(float gridp1, float gridp2, float gridp3)
            {
                em0 = new EMData
                {
                    id = 0,
                    a_current = gridp1 / 230.0f,
                    a_voltage = 230,
                    a_act_power = gridp1,
                    a_aprt_power = gridp1,
                    a_pf = 1,
                    a_freq = 50,

                    b_current = gridp2 / 230.0f,
                    b_voltage = 230,
                    b_act_power = gridp2,
                    b_aprt_power = gridp2,
                    b_pf = 1,
                    b_freq = 50,

                    c_current = gridp3 / 230.0f,
                    c_voltage = 230,
                    c_act_power = gridp3,
                    c_aprt_power = gridp3,
                    c_pf = 1,
                    c_freq = 50,


                    total_current = gridp1 + gridp2 + gridp3,
                    total_act_power = gridp1 + gridp2 + gridp3,
                    total_aprt_power = gridp1 + gridp2 + gridp3
                };
            }

            public struct EMData
            {
                public int id { get; set; }

                public float a_current { get; set; }
                public float a_voltage { get; set; }
                public float a_act_power { get; set; }
                public float a_aprt_power { get; set; }
                public float a_pf { get; set; }
                public float a_freq { get; set; }

                public float b_current { get; set; }
                public float b_voltage { get; set; }
                public float b_act_power { get; set; }
                public float b_aprt_power { get; set; }
                public float b_pf { get; set; }
                public float b_freq { get; set; }

                public float c_current { get; set; }
                public float c_voltage { get; set; }
                public float c_act_power { get; set; }
                public float c_aprt_power { get; set; }
                public float c_pf { get; set; }
                public float c_freq { get; set; }



                public float total_current { get; set; }
                public float total_act_power { get; set; }
                public float total_aprt_power { get; set; }
            }

            [JsonPropertyName("em:0")]
            public EMData em0 { get; set; }

            public struct SysInfo
            {
                public SysInfo()
                {
                }

                public string mac { get; set; } = "123412341234";
            }

            public SysInfo sys { get; set; }
        }


        float _powerPhase1 = 0;
        float _powerPhase2 = 0;
        float _powerPhase3 = 0;

        private string _serialNumber;


        public ShellyEmulator()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _routes = new Dictionary<string, Func<HttpListenerContext, Task>>();


            _listener = new HttpListener();
            var hostname = System.Net.Dns.GetHostName();


            if (hostname == "LAMBDA")
            {
                _listener.Prefixes.Add("http://*:80/");
                _serialNumber = "123412341234";
            }
            else
            {
                _listener.Prefixes.Add("http://*:10000/");
                _serialNumber = "ABCDABCDABCD";
            }

            try
            {
                _listener.Start();
            } 
            catch (System.Net.HttpListenerException ex)
            {
                Console.WriteLine(ex.ToString());
                return; // Emulator just won't run, if we cannot listen
            }
          

            Console.WriteLine("HTTP Server started on:");
            foreach (string prefix in _listener.Prefixes)
            {
                Console.WriteLine($"  {prefix}");
            }

            // Handle requests concurrently
            var tasks = new List<Task>();
            for (int i = 0; i < 10; i++) //10 concurrent connections at a time
            {
                tasks.Add(HandleIncomingConnections());
            }

            Task.Run(async () =>
            {
                /*
                using var udpListener = new UdpClient(1010);

                Console.WriteLine($"[INFO] UDP Listener started. Listening on port {1010}...");
                Console.WriteLine("[INFO] Press Ctrl+C to exit.\n");

                try
                {
                    while (true)
                    {
                        // Await incoming data asynchronously without blocking the main execution thread
                        UdpReceiveResult receivedResult = await udpListener.ReceiveAsync();

                        // Extract the raw byte array data
                        byte[] bytes = receivedResult.Buffer;

                        // Safely convert bytes to a human-readable UTF-8 string
                        string message = Encoding.UTF8.GetString(bytes);

                        // Identify where the message came from
                        IPEndPoint sender = receivedResult.RemoteEndPoint;

                        // Print the details directly to the console
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Received from {sender.Address}:{sender.Port}");
                        Console.WriteLine($"Length: {bytes.Length} bytes");
                        Console.WriteLine($"Data:   {message}");
                        Console.WriteLine(new string('-', 40));
                    }
                }
                catch (SocketException ex)
                {
                    Console.WriteLine($"[ERROR] Socket error occurred: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] An unexpected error occurred: {ex.Message}");
                }
                */

                await Task.WhenAll(tasks);
            });


        }


        private async Task HandleIncomingConnections()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    // Handle each request asynchronously
                    _ = Task.Run(() => ProcessRequest(context));
                }
                catch (ObjectDisposedException)
                {
                    // Listener was stopped
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error getting context: {ex.Message}");
                }
            }
        }


        private async Task ProcessRequest(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;

                // Handle OPTIONS preflight request for CORS
                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 200;
                    response.Close();
                    return;
                }

                //Console.WriteLine($"{DateTimeOffset.Now: HH:mm:ss.fff} {request.HttpMethod} {request.Url.LocalPath}");

                // Route the request
                string routeKey = $"{request.HttpMethod} {request.Url.LocalPath}";


                //GET / rpc / Shelly.GetStatus
                //GET / rpc / Shelly.GetDeviceInfo


                //var lastCopy = _powerLastMeter;
                //float delta = _powerLive.TotalUsage() - lastCopy.TotalUsage();
                //float gridCompensated = lastCopy.Grid + delta;
                //Console.WriteLine($"Report {gridCompensated}");

                response.StatusCode = 200;

                if (request.Url.LocalPath.EndsWith(".GetStatus"))
                {
                    var data = new ShellyStatus(_powerPhase1, _powerPhase2, _powerPhase3);
                    await WriteJsonResponse(response, data);
                }
                else if (request.Url.LocalPath.EndsWith(".GetDeviceInfo"))
                {
                    var data = new
                    {
                        id = $"ShellyPro3EM-{_serialNumber}",
                        mac = _serialNumber,
                        slot = 1,
                        model = "SPEM-003CEBEU",
                        gen = 2,
                        fw_id = "20250924-062729/1.7.1-gd336f31",
                        ver = "1.4.4",
                        app = "Pro3EM",
                        auth_en = false,
                        profile = "triphase",
                        debug = _debugData
                    };
                    await WriteJsonResponse(response, data);
                }
                else
                {
                    var data = new MeterReading(_powerPhase1, _powerPhase2, _powerPhase3);
                    await WriteJsonResponse(response, data);
                }

                response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing request: {ex.Message}");
                try
                {
                    context.Response.StatusCode = 500;
                    await WriteJsonResponse(context.Response, new { error = "Internal server error" });
                }
                catch
                {
                    // Ignore if response is already closed
                }
            }
        }

        // Helper Methods
        private async Task<string> ReadRequestBody(HttpListenerRequest request)
        {
            if (!request.HasEntityBody)
                return string.Empty;

            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            return await reader.ReadToEndAsync();
        }

        private async Task WriteResponse(HttpListenerResponse response, string content)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(content);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();
        }

        private async Task WriteJsonResponse(HttpListenerResponse response, object data)
        {
            response.ContentType = "application/json";
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
            await WriteResponse(response, json);
        }


        private object _debugData;


        public void SetPowerPhase(int phase, float power)
        {
            switch (phase)
            {
                case 0: _powerPhase1 = power; break;
                case 1: _powerPhase2 = power; break;
                case 2: _powerPhase3 = power; break;
                default: throw new ArgumentOutOfRangeException("invalid phase");
            }
        }

        public void SetDebugData(object o)
        {
            _debugData = o;
        }
    }
}
