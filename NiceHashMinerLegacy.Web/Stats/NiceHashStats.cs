﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NiceHashMinerLegacy.Common.Configs;
using NiceHashMinerLegacy.Common.Enums;
using NiceHashMinerLegacy.Common.Utils;
using NiceHashMinerLegacy.Web.Stats.Models;
using NiceHashMinerLegacy.Web.Switching;
using WebSocketSharp;

[assembly:InternalsVisibleTo("NiceHashMinerLegacy.Web.Tests")]

namespace NiceHashMinerLegacy.Web.Stats
{
    public class SocketEventArgs : EventArgs
    {
        public readonly string Message;
        public bool Enabled;

        public SocketEventArgs(string message)
        {
            Message = message;
        }
    }

    public static class NiceHashStats
    {
        #region JSON Models
#pragma warning disable 649, IDE1006
        private class NicehashCredentials
        {
            public string method = "credentials.set";
            public string btc;
            public string worker;
        }

        private class NicehashDeviceStatus
        {
            public string method = "devices.status";
            public List<JArray> devices;
        }
        public class ExchangeRateJson
        {
            public List<Dictionary<string, string>> exchanges { get; set; }
            public Dictionary<string, double> exchanges_fiat { get; set; }
        }
#pragma warning restore 649, IDE1006
        #endregion

        private const int DeviceUpdateLaunchDelay = 20 * 1000;
        private const int DeviceUpdateInterval = 60 * 1000;
        
        public static double Balance { get; private set; }
        public static string Version { get; private set; }
        public static bool IsAlive => _socket?.IsAlive ?? false;

        // Event handlers for socket
        public static event EventHandler OnBalanceUpdate;

        public static event EventHandler OnSmaUpdate;
        public static event EventHandler OnVersionUpdate;
        public static event EventHandler OnConnectionLost;
        public static event EventHandler OnConnectionEstablished;
        public static event EventHandler<SocketEventArgs> OnVersionBurn;
        public static event EventHandler OnExchangeUpdate;

        public static Action<string, bool> SetDevicesEnabled; 

        private static NiceHashSocket _socket;
        
        private static System.Threading.Timer _deviceUpdateTimer;

        private static Func<List<JArray>> _devStatus;

        public static void StartConnection(string address, string ver, Func<List<JArray>> devStatus)
        {
            _devStatus = devStatus;

            if (_socket == null)
            {
                _socket = new NiceHashSocket(address, ver);
                _socket.OnConnectionEstablished += SocketOnOnConnectionEstablished;
                _socket.OnDataReceived += SocketOnOnDataReceived;
                _socket.OnConnectionLost += SocketOnOnConnectionLost;
            }
            _socket.StartConnection();
            _deviceUpdateTimer = new System.Threading.Timer(DeviceStatus_Tick, null, DeviceUpdateInterval, DeviceUpdateInterval);
        }

        #region Socket Callbacks

        private static void SocketOnOnConnectionLost(object sender, EventArgs eventArgs)
        {
            OnConnectionLost?.Invoke(sender, eventArgs);
        }

        private static void SocketOnOnDataReceived(object sender, MessageEventArgs e)
        {
            var isRpc = false;
            try
            {
                if (e.IsText)
                {
                    isRpc = ProcessData(e.Data);
                }

                if (isRpc)
                {
                    SendExecuted();
                }
            } catch (Exception er)
            {
                Helpers.ConsolePrint("SOCKET", er.ToString());
                if (isRpc)
                {
                    // TODO report RPC error?
                }
            }
        }

        internal static bool ProcessData(string data)
        {
            Helpers.ConsolePrint("SOCKET", "Received: " + data);
            dynamic message = JsonConvert.DeserializeObject(data);
            switch (message.method.Value)
            {
                case "sma":
                {
                    // Try in case stable is not sent, we still get updated paying rates
                    try
                    {
                        var stable = JsonConvert.DeserializeObject(message.stable.Value);
                        SetStableAlgorithms(stable);
                    }
                    catch
                    { }
                    SetAlgorithmRates(message.data);
                    break;
                }

                case "balance":
                    SetBalance(message.value.Value);
                    break;
                case "versions":
                    SetVersion(message.legacy.Value);
                    break;
                case "burn":
                    OnVersionBurn?.Invoke(null, new SocketEventArgs(message.message.Value));
                    break;
                case "exchange_rates":
                    SetExchangeRates(message.data.Value);
                    break;
                case "essentials":
                    var ess = JsonConvert.DeserializeObject<EssentialsCall>(data);
                    if (ess.Params.First()[1] is string ver)
                    {
                        SetVersion(ver);
                    }

                    break;
                case "mining.set.username":
                    var user = (string) message.username;

                    if (!BitcoinAddress.ValidateBitcoinAddress(user))
                        throw new RpcException("Bitcoin address invalid", 1);

                    ConfigManager.GeneralConfig.BitcoinAddress = user;
                    return true;
                case "mining.set.worker":
                    var worker = (string) message.worker;

                    if (!BitcoinAddress.ValidateWorkerName(worker))
                        throw new RpcException("Worker name invalid", 1);

                    ConfigManager.GeneralConfig.WorkerName = worker;
                    return true;
                case "mining.enable":
                    SetDevicesEnabled((string) message.device, true);
                    return true;
                case "mining.disable":
                    SetDevicesEnabled((string) message.device, false);
                    return true;
            }

            return false;
        }

        private static void SocketOnOnConnectionEstablished(object sender, EventArgs e)
        {
            DeviceStatus_Tick(null); // Send device to populate rig stats

            OnConnectionEstablished?.Invoke(null, EventArgs.Empty);
        }

        #endregion

        #region Incoming socket calls

        private static void SetAlgorithmRates(JArray data)
        {
            try
            {
                var payingDict = new Dictionary<AlgorithmType, double>();
                if (data != null)
                {
                    foreach (var algo in data)
                    {
                        var algoKey = (AlgorithmType) algo[0].Value<int>();
                        payingDict[algoKey] = algo[1].Value<double>();
                    }
                }

                NHSmaData.UpdateSmaPaying(payingDict);
                
                OnSmaUpdate?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception e)
            {
                Helpers.ConsolePrint("SOCKET", e.ToString());
            }
        }

        private static void SetStableAlgorithms(JArray stable)
        {
            var stables = stable.Select(algo => (AlgorithmType) algo.Value<int>());
            NHSmaData.UpdateStableAlgorithms(stables);
        }

        private static void SetBalance(string balance)
        {
            try
            {
                if (double.TryParse(balance, NumberStyles.Float, CultureInfo.InvariantCulture, out var bal))
                {
                    Balance = bal;
                    OnBalanceUpdate?.Invoke(null, EventArgs.Empty);
                }
            }
            catch (Exception e)
            {
                Helpers.ConsolePrint("SOCKET", e.ToString());
            }
        }

        private static void SetVersion(string version)
        {
            Version = version;
            OnVersionUpdate?.Invoke(null, EventArgs.Empty);
        }

        private static void SetExchangeRates(string data)
        {
            try
            {
                var exchange = JsonConvert.DeserializeObject<ExchangeRateJson>(data);
                if (exchange?.exchanges_fiat == null || exchange.exchanges == null) return;
                foreach (var exchangePair in exchange.exchanges)
                {
                    if (!exchangePair.TryGetValue("coin", out var coin) || coin != "BTC" ||
                        !exchangePair.TryGetValue("USD", out var usd) || 
                        !double.TryParse(usd, NumberStyles.Float, CultureInfo.InvariantCulture, out var usdD))
                        continue;

                    ExchangeRateApi.UsdBtcRate = usdD;
                    break;
                }

                ExchangeRateApi.UpdateExchangesFiat(exchange.exchanges_fiat);

                OnExchangeUpdate?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception e)
            {
                Helpers.ConsolePrint("SOCKET", e.ToString());
            }
        }

        #endregion

        #region Outgoing socket calls

        public static void SetCredentials(string btc, string worker)
        {
            var data = new NicehashCredentials
            {
                btc = btc,
                worker = worker
            };
            if (BitcoinAddress.ValidateBitcoinAddress(data.btc) && BitcoinAddress.ValidateWorkerName(worker))
            {
                var sendData = JsonConvert.SerializeObject(data);

                // Send as task since SetCredentials is called from UI threads
                Task.Factory.StartNew(() => _socket?.SendData(sendData));
            }
        }

        private static void DeviceStatus_Tick(object state)
        {

            var data = new NicehashDeviceStatus
            {
                devices = _devStatus()
            };
            var sendData = JsonConvert.SerializeObject(data);
            // This function is run every minute and sends data every run which has two auxiliary effects
            // Keeps connection alive and attempts reconnection if internet was dropped
            _socket?.SendData(sendData);
        }

        private static void SendExecuted(int code = 0, string message = null)
        {
            var data = new ExecutedCall(code, message).Serialize();
            _socket?.SendData(data);
        }

        #endregion

        public static string GetNiceHashApiData(string url, string worker)
        {
            var responseFromServer = "";
            try
            {
                //var activeMinersGroup = MinersManager.GetActiveMinersGroup();

                var wr = (HttpWebRequest) WebRequest.Create(url);
                //wr.UserAgent = "NiceHashMiner/" + Application.ProductVersion;
                if (worker.Length > 64) worker = worker.Substring(0, 64);
                wr.Headers.Add("NiceHash-Worker-ID", worker);
                //wr.Headers.Add("NHM-Active-Miners-Group", activeMinersGroup);
                wr.Timeout = 30 * 1000;
                var response = wr.GetResponse();
                var ss = response.GetResponseStream();
                if (ss != null)
                {
                    ss.ReadTimeout = 20 * 1000;
                    var reader = new StreamReader(ss);
                    responseFromServer = reader.ReadToEnd();
                    if (responseFromServer.Length == 0 || responseFromServer[0] != '{')
                        throw new Exception("Not JSON!");
                    reader.Close();
                }
                response.Close();
            }
            catch (Exception ex)
            {
                Helpers.ConsolePrint("NICEHASH", ex.Message);
                return null;
            }

            return responseFromServer;
        }
    }
}
