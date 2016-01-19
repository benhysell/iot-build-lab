using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.System.Threading;
using Microsoft.Maker.Sparkfun.WeatherShield;
using Newtonsoft.Json;

// The Background Application template is documented at http://go.microsoft.com/fwlink/?LinkID=533884&clcid=0x409

namespace WeatherStationBackground
{
    public sealed partial class StartupTask : IBackgroundTask
    {
        private readonly int i2cReadIntervalSeconds = 2;
        private ThreadPoolTimer i2cTimer;
        private Mutex mutex;
        private string mutexId = "WeatherStation";
        private readonly int port = 50001;
        private ThreadPoolTimer SasTokenRenewTimer;
        private HttpServer server;
        private WeatherShield shield = new WeatherShield("I2C1", 6, 5);
        private BackgroundTaskDeferral taskDeferral;
        private readonly WeatherData weatherData = new WeatherData();

        // Hard coding guid for sensors. Not an issue for this particular application which is meant for testing and demos
        private List<ConnectTheDotsSensor> sensors = new List<ConnectTheDotsSensor> {
            //Format for a new sensor is as follows:
            //new ConnectTheDotsSensor("YOUR_GUID_HERE", "VALUE_DESCRIPTOR", "UNIT_OF_MEASUREMENT");
            new ConnectTheDotsSensor("2298a348-e2f9-4438-ab23-82a3930662ab", "Altitude", "m"),
            new ConnectTheDotsSensor("2298a348-e2f9-4438-ab23-82a3930662ac", "Humidity", "%RH"),
            new ConnectTheDotsSensor("2298a348-e2f9-4438-ab23-82a3930662ad", "Pressure", "kPa"),
            new ConnectTheDotsSensor("2298a348-e2f9-4438-ab23-82a3930662ae", "Temperature", "C"),
        };

        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            // Ensure our background task remains running
            taskDeferral = taskInstance.GetDeferral();

            // Mutex will be used to ensure only one thread at a time is talking to the shield / isolated storage
            mutex = new Mutex(false, mutexId);

            // Initialize ConnectTheDots Settings
            //Endpoint=sb://viewpoint-ns.servicebus.windows.net/;SharedAccessKeyName=D1;SharedAccessKey=25iB0rR9kvEyPaRu4uu26HgT5JpRy7qPEabMGjh9buY=
          

            SaveSettings();

            // Initialize WeatherShield
            await shield.BeginAsync();

            // Create a timer-initiated ThreadPool task to read data from I2C
            i2cTimer = ThreadPoolTimer.CreatePeriodicTimer(PopulateWeatherData, TimeSpan.FromSeconds(i2cReadIntervalSeconds));

            // Start the server
            server = new HttpServer(port);
            var asyncAction = ThreadPool.RunAsync((w) => { server.StartServer(shield, weatherData); });

            // Task cancellation handler, release our deferral there 
            taskInstance.Canceled += OnCanceled;
            // Create a timer-initiated ThreadPool task to renew SAS token regularly
             SasTokenRenewTimer = ThreadPoolTimer.CreatePeriodicTimer(RenewSasToken, TimeSpan.FromMinutes(15));
        }

        private string GetHostName()
        {
            foreach (HostName name in NetworkInformation.GetHostNames())
            {
                if (HostNameType.DomainName == name.Type)
                {
                    return name.DisplayName;
                }
            }

            return "minwinpc";
        }

        private void OnCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            // Relinquish our task deferral
            taskDeferral.Complete();
        }

        private void PopulateWeatherData(ThreadPoolTimer timer)
        {
            bool hasMutex = false;

            try
            {
                hasMutex = mutex.WaitOne(1000);
                if (hasMutex)
                {
                    weatherData.TimeStamp = DateTime.Now.ToLocalTime().ToString();

                    shield.BlueLedPin.Write(Windows.Devices.Gpio.GpioPinValue.High);

                    weatherData.Altitude = shield.Altitude;
                    weatherData.BarometricPressure = shield.Pressure;
                    weatherData.CelsiusTemperature = shield.Temperature;
                    weatherData.FahrenheitTemperature = (weatherData.CelsiusTemperature * 9 / 5) + 32;
                    weatherData.Humidity = shield.Humidity;

                    shield.BlueLedPin.Write(Windows.Devices.Gpio.GpioPinValue.Low);

                    // Push the WeatherData cloud storage (viewable at http://iotbuildlab.azurewebsites.net/)
                    SendDataToConnectTheDots();
                }
            }
            finally
            {
                if (hasMutex)
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        private void RenewSasToken(ThreadPoolTimer timer)
        {
            bool hasMutex = false;

            try
            {
                hasMutex = mutex.WaitOne(1000);
                if (hasMutex)
                {
                    UpdateSasToken();
                }
            }
            finally
            {
                if (hasMutex)
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        private async void SendDataToConnectTheDots()
        {
            ConnectTheDotsSensor sensor;
            string time = DateTime.UtcNow.ToString("o");

            // Send the altitude data
            sensor = sensors.Find(item => item.measurename == "Altitude");
            if (sensor != null)
            {
                sensor.value = weatherData.Altitude;
                sensor.timecreated = time;
                weatherData.SendResult = await SendMessage(JsonConvert.SerializeObject(sensor));
            }

            // Send the humidity data
            sensor = sensors.Find(item => item.measurename == "Humidity");
            if (sensor != null)
            {
                sensor.value = weatherData.Humidity;
                sensor.timecreated = time;
                weatherData.SendResult = await SendMessage(JsonConvert.SerializeObject(sensor));
            }

            // Sending the pressure data
            sensor = sensors.Find(item => item.measurename == "Pressure");
            if (sensor != null)
            {
                sensor.value = (weatherData.BarometricPressure / 1000);
                sensor.timecreated = time;
                weatherData.SendResult = await SendMessage(JsonConvert.SerializeObject(sensor));
            }

            // Sending the temperature data
            sensor = sensors.Find(item => item.measurename == "Temperature");
            if (sensor != null)
            {
                sensor.value = weatherData.CelsiusTemperature;
                sensor.timecreated = time;
                weatherData.SendResult = await SendMessage(JsonConvert.SerializeObject(sensor));
            }
        }
    }
}
