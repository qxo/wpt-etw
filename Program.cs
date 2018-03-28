﻿using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace wpt_etw
{
    class Program
    {
        static Dictionary<String, int> IEEvents = new Dictionary<String, int>() {
            { "Mshtml_CWindow_SuperNavigate2/Start", 70 },
            { "Mshtml_BFCache", 574 },
            { "Mshtml_WebOCEvents_BeforeNavigate", 730 },
            { "Mshtml_CDoc_Navigation", 609 },
            { "Mshtml_WebOCEvents_DOMContentLoaded", 739 },
            { "Mshtml_WebOCEvents_DocumentComplete", 735 },
            { "Mshtml_WebOCEvents_NavigateComplete", 732 },
            { "Mshtml_CMarkup_LoadEvent_Start/Start", 9 },
            { "Mshtml_CMarkup_LoadEvent_Stop/Stop", 10 },
            { "Mshtml_CMarkup_DOMContentLoadedEvent_Start/Start", 7 },
            { "Mshtml_CMarkup_DOMContentLoadedEvent_Stop/Stop", 8 },
            { "Mshtml_NotifyGoesInteractive/Start", 57 }
        };

        static Dictionary<String, int> WinInetEvents = new Dictionary<String, int>() {
            { "WININET_DNS_QUERY/Start", 304 },
            { "WININET_DNS_QUERY/Stop", 305 },
            { "Wininet_Getaddrinfo/Start", 1051 },
            { "Wininet_Getaddrinfo/Stop", 1052 },
            { "Wininet_SocketConnect/Start", 1059 },
            { "Wininet_SocketConnect/Stop", 1060 },
            { "WININET_TCP_CONNECTION/Start", 301 },
            { "WININET_TCP_CONNECTION/Stop", 303 },
            { "WININET_TCP_CONNECTION/Fail", 302 },
            { "Wininet_Connect/Stop", 1046 },
            { "WININET_HTTPS_NEGOTIATION/Start", 701},
            { "WININET_HTTPS_NEGOTIATION/Stop", 702 },
            { "WININET_REQUEST_HEADER", 546 },
            { "WININET_RESPONSE_HEADER", 211 },
            { "Wininet_SendRequest/Start", 1007 },
            { "Wininet_SendRequest/Stop", 1008 },
            { "Wininet_SendRequest_Main", 1031 },
            { "Wininet_ReadData", 1037 },
            { "Wininet_UsageLogRequest", 1057 },
            { "Wininet_LookupConnection/Stop", 1048 },
            { "WININET_STREAM_DATA_INDICATED", 1064 }
        };

        static TraceEventSession session;
        static bool must_exit = false;
        private static Mutex mutex = new Mutex();
        static string events = "";
        static string body_dir = "";
        static Dictionary<string, CustomProvider> customProviders = new Dictionary<string, CustomProvider>();
        static string customProvidersConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @".\customProviders.json");

        static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--bodies" && System.IO.Directory.Exists(args[1]))
                body_dir = args[1];

            // read settings for custom ETW providers            
            if (File.Exists(customProvidersConfigPath))
            {
                try
                {
                    customProviders = JsonConvert.DeserializeObject<Dictionary<string, CustomProvider>>(File.ReadAllText(customProvidersConfigPath));
                }
                catch { }
            }

            // create a real time user mode session
            using (session = new TraceEventSession("wpt-etw"))
            {
                session.StopOnDispose = true;
                // Set up Ctrl-C to stop the session
                Console.CancelKeyPress += (object s, ConsoleCancelEventArgs cancel_args) => session.Stop();

                session.Source.Dynamic.All += delegate (TraceEvent data)
                {
                    try
                    {
                        bool keep = false;
                        if (data.ProviderName == "Microsoft-Windows-WinINet-Capture")
                        {
                            if (data.ActivityID != Guid.Empty && data.EventName == "EventID(2004)")
                            {
                                var raw = data.PayloadByName("Payload") as byte[];
                                if (raw != null && raw.Length > 0)
                                {
                                    string activity = data.ActivityID.ToString("D");
                                    string path = body_dir + "\\" + activity;
                                    try
                                    {
                                        using (var stream = new FileStream(path, FileMode.Append))
                                        {
                                            stream.Write(raw, 0, raw.Length);
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        else if (data.ProviderName == "Microsoft-IE" &&
                            IEEvents.ContainsKey(data.EventName))
                        {
                            keep = true;
                        }
                        else if (data.ProviderName == "Microsoft-Windows-WinINet" &&
                                 WinInetEvents.ContainsKey(data.EventName))
                        {
                            keep = true;
                        }
                        else if (customProviders.ContainsKey(data.ProviderName) &&
                                customProviders[data.ProviderName].EventNames.Contains(data.EventName))
                        {
                            keep = true;
                        }

                        if (keep)
                        {
                            Dictionary<string, dynamic> evt = new Dictionary<string, dynamic>();
                            evt["Provider"] = data.ProviderName;
                            evt["Event"] = data.EventName;
                            evt["ts"] = data.TimeStampRelativeMSec;
                            if (data.ActivityID != Guid.Empty)
                                evt["Activity"] = data.ActivityID.ToString("D");
                            if (data.RelatedActivityID != Guid.Empty)
                                evt["RelatedActivity"] = data.RelatedActivityID;
                            if (data.ProcessID >= 0)
                                evt["pid"] = data.ProcessID;
                            if (data.ThreadID >= 0)
                                evt["tid"] = data.ThreadID;
                            if (data.PayloadNames.Count() > 0)
                            {
                                Dictionary<string, dynamic> values = new Dictionary<string, dynamic>();
                                foreach (string name in data.PayloadNames)
                                {
                                    values[name] = data.PayloadByName(name);
                                }
                                // Special-case a few events where teh default decoder doesn't work correctly
                                if (data.ProviderName == "Microsoft-Windows-WinINet")
                                {
                                    try
                                    {
                                        //ExtractWinInetValues(data, ref values);
                                    }
                                    catch { }
                                }
                                evt["data"] = values;
                            }

                            //evt["ascii"] = System.Text.Encoding.ASCII.GetString(data.EventData());
                            //evt["raw"] = data.EventData();
                            string json = JsonConvert.SerializeObject(evt) + "\n";
                            // Throw out anything that is for the local server on http://127.0.0.1:8888
                            if (json.IndexOf("http://127.0.0.1:8888") == -1)
                            {
                                mutex.WaitOne();
                                events += json;
                                mutex.ReleaseMutex();
                            }
                            //Debug.WriteLine(json.Trim());
                            //Console.WriteLine(json.Trim());
                        }
                    }
                    catch { }
                };

                if (body_dir.Length > 0)
                    session.EnableProvider("Microsoft-Windows-WinInet-Capture");

                var WinInetProviderFilterOptions = new TraceEventProviderOptions()
                {
                    EventIDsToEnable = new List<int>(WinInetEvents.Values)
                };
                session.EnableProvider("Microsoft-Windows-WinINet", TraceEventLevel.Informational, ulong.MaxValue, WinInetProviderFilterOptions);

                var IEProviderFilterOptions = new TraceEventProviderOptions()
                {
                    EventIDsToEnable = new List<int>(IEEvents.Values)
                };
                session.EnableProvider("Microsoft-IE", TraceEventLevel.Informational, 0x4001302, IEProviderFilterOptions);

                if (customProviders.Count > 0)
                {
                    foreach (var provider in customProviders)
                    {
                        var customProviderFilterOptions = new TraceEventProviderOptions()
                        {
                            EventIDsToEnable = new List<int>(provider.Value.EventIDs)
                        };

                        session.EnableProvider(provider.Key,
                                            (TraceEventLevel)provider.Value.Verbosity,
                                            provider.Value.Filter,
                                            customProviderFilterOptions);
                    }
                }

                must_exit = false;
                var thread = new Thread(ThreadProc);
                thread.Start();
                try
                {
                    session.Source.Process();   // Listen (forever) for events
                }
                catch { }
                must_exit = true;
                thread.Join();
            }
        }

        private static void ThreadProc()
        {
            string done_file = AppDomain.CurrentDomain.BaseDirectory + "wpt-etw.done";
            Console.WriteLine("Forwarding ETW events to http://127.0.0.1:8888/");
            Console.WriteLine("To exit, hit ctrl-C or create the file " + done_file);
            int count = 0;
            HttpClient wptagent = new HttpClient();
            var content = new StringContent("{\"message\": \"wptagent.started\"}", Encoding.UTF8, "application/json");
            try
            {
                var response = wptagent.PostAsync("http://127.0.0.1:8888/etw", content).Result;
            }
            catch { }
            do
            {
                Thread.Sleep(100);
                try
                {
                    string buff = "";
                    mutex.WaitOne();
                    if (events.Length > 0)
                    {
                        buff = events;
                        events = "";
                    }
                    mutex.ReleaseMutex();

                    if (buff.Length > 0)
                    {
                        content = new StringContent(buff, Encoding.UTF8, "application/json");
                        try
                        {
                            var response = wptagent.PostAsync("http://127.0.0.1:8888/etw", content).Result;
                        }
                        catch { }
                    }

                    // Check to see if we need to exit every 1 second (10 loops through)
                    count++;
                    if (count >= 10)
                    {
                        if (File.Exists(done_file))
                        {
                            try
                            {
                                File.Delete(done_file);
                            }
                            catch
                            {
                            }
                            must_exit = true;
                        }
                        count = 0;
                    }
                }
                catch { }
            } while (!must_exit);
            Debug.WriteLine("Exiting...");
            Console.WriteLine("Exiting...");
            try
            {
                session.Stop();
            }
            catch { }
        }

        /*
         * Handle messages that the underlying library doesn't handle correctly (mostly ASCII strings)
         **/
        static void ExtractWinInetValues(TraceEvent data, ref Dictionary<string, dynamic> values)
        {
            if (data.EventName == "WININET_REQUEST_HEADER" || data.EventName == "WININET_RESPONSE_HEADER")
            {
                byte[] raw = data.EventData().Skip(10).ToArray();
                values["Headers"] = System.Text.Encoding.ASCII.GetString(raw);
            }
            else if (data.EventName == "WININET_DNS_QUERY/Start")
            {
                byte[] raw = data.EventData().Skip(2).ToArray();
                int count = 0;
                foreach (byte c in raw)
                {
                    if (c == 0)
                        break;
                    count++;
                }
                values["HostName"] = System.Text.Encoding.ASCII.GetString(raw.Take(count).ToArray());
            }
            else if (data.EventName == "WININET_DNS_QUERY/Stop")
            {
                byte[] raw = data.EventData().Skip(2).ToArray();
                int count = 0;
                foreach (byte c in raw)
                {
                    if (c == 0)
                        break;
                    count++;
                }
                values["HostName"] = System.Text.Encoding.ASCII.GetString(raw.Take(count).ToArray());
                // Find the beginning of the address list (numerical characters)
                raw = raw.Skip(count).ToArray();
                count = 0;
                foreach (byte c in raw)
                {
                    if (c >= '0' && c <= '9')
                        break;
                    count++;
                }
                raw = raw.Skip(count).ToArray();
                values["AddressList"] = System.Text.Encoding.ASCII.GetString(raw);
            }
            else if (data.EventName == "WININET_TCP_CONNECTION/Start")
            {
                byte[] raw = data.EventData().Skip(2).ToArray();
                int count = 0;
                foreach (byte c in raw)
                {
                    if (c == 0)
                        break;
                    count++;
                }
                values["ServerName"] = System.Text.Encoding.ASCII.GetString(raw.Take(count).ToArray());
            }
            else if (data.EventName == "Wininet_SendRequest/Stop")
            {
                byte[] raw = data.EventData().Reverse().ToArray();
                int count = 0;
                foreach (byte c in raw)
                {
                    if (c == 0)
                        break;
                    count++;
                }
                raw = raw.Take(count).Reverse().ToArray();
                values["StatusLine"] = System.Text.Encoding.ASCII.GetString(raw);
            }
            else if (data.EventName == "Wininet_Connect/Stop")
            {
                byte[] raw = data.EventData();
                values["LocalAddress"] = String.Format("{0}.{1}.{2}.{3}", values["LocalAddress"][4], values["LocalAddress"][5], values["LocalAddress"][6], values["LocalAddress"][7]);
                values["RemoteAddress"] = String.Format("{0}.{1}.{2}.{3}", values["RemoteAddress"][4], values["RemoteAddress"][5], values["RemoteAddress"][6], values["RemoteAddress"][7]);
            }
        }
    }
}
