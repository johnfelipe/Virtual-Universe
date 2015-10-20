/*
 * Copyright (c) Contributors, http://virtual-planets.org/, http://whitecore-sim.org/, http://aurora-sim.org
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the Virtual Universe Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */


using System;
using System.Collections.Generic;
using System.IO;
using System.Timers;
using Nini.Config;
using OpenMetaverse;
using Universe.Framework.Configuration;
using Universe.Framework.ConsoleFramework;
using Universe.Framework.ModuleLoader;
using Universe.Framework.Modules;
using Universe.Framework.SceneInfo;
using Universe.Framework.Servers;
using Universe.Framework.Servers.HttpServer;
using Universe.Framework.Servers.HttpServer.Interfaces;
using Universe.Framework.Services;
using Universe.Framework.Services.ClassHelpers.Other;
using Universe.Framework.Utilities;
using Universe.Services.DataService;

namespace Universe.Simulation.Base
{
    public class MinimalSimulationBase : ISimulationBase
    {
        protected string m_startupCommandsFile;
        protected string m_shutdownCommandsFile;
        protected string m_TimerScriptFileName = "disabled";
        protected int m_TimerScriptTime = 20;
        protected IHttpServer m_BaseHTTPServer;
        protected Timer m_TimerScriptTimer;
        protected ConfigurationLoader m_configurationLoader;

        /// <value>
        ///     The config information passed into the Universe server.
        /// </value>
        protected IConfigSource m_config;

        protected IConfigSource m_original_config;

        public IConfigSource ConfigSource
        {
            get { return m_config; }
            set { m_config = value; }
        }

        /// <summary>
        ///     Server version information.  Usually VersionInfo + information about git commit, operating system, etc.
        /// </summary>
        protected string m_version;

        public string Version
        {
            get { return m_version; }
        }

        protected string m_defaultDataPath = Constants.DEFAULT_DATA_DIR;
        public string DefaultDataPath
        { 
            get { return m_defaultDataPath;}
            set { m_defaultDataPath = value;}
        }

        protected IRegistryCore m_applicationRegistry = new RegistryCore();

        public IRegistryCore ApplicationRegistry
        {
            get { return m_applicationRegistry; }
        }

        protected UniverseEventManager m_eventManager = new UniverseEventManager();

        public UniverseEventManager EventManager
        {
            get { return m_eventManager; }
        }

        /// <summary>
        ///     Time at which this server was started
        /// </summary>
        protected DateTime m_StartupTime;

        public DateTime StartupTime
        {
            get { return m_StartupTime; }
        }

        public IHttpServer HttpServer
        {
            get { return m_BaseHTTPServer; }
        }

        protected Dictionary<uint, IHttpServer> m_Servers =
            new Dictionary<uint, IHttpServer>();

        protected uint m_Port;

        public uint DefaultPort
        {
            get { return m_Port; }
        }

        protected string[] m_commandLineParameters = null;

        public string[] CommandLineParameters
        {
            get { return m_commandLineParameters; }
        }

        protected string m_pidFile = String.Empty;

        protected string m_consolePrompt = "";
        protected List<Type> m_dataPlugins;
        protected List<Type> m_servicePlugins;

        public MinimalSimulationBase(string consolePrompt, List<Type> dataPlugins, List<Type> servicePlugins)
        {
            m_consolePrompt = consolePrompt;
            m_dataPlugins = dataPlugins;
            m_servicePlugins = servicePlugins;
        }

        /// <summary>
        ///     Do the initial setup for the application
        /// </summary>
        /// <param name="originalConfig"></param>
        /// <param name="configSource"></param>
        /// <param name="cmdParams"></param>
        /// <param name="configLoader"></param>
        public virtual void Initialize(IConfigSource originalConfig, IConfigSource configSource, string[] cmdParams,
                                       ConfigurationLoader configLoader)
        {
            m_commandLineParameters = cmdParams;
            m_StartupTime = DateTime.Now;
            m_version = VersionInfo.Version;
            m_original_config = originalConfig;
            m_config = configSource;
            m_configurationLoader = configLoader;

            // This thread will go on to become the console listening thread
            System.Threading.Thread.CurrentThread.Name = "ConsoleThread";

            //Register the interface
            ApplicationRegistry.RegisterModuleInterface<ISimulationBase>(this);

            Configuration(configSource);

            InitializeModules();

            RegisterConsoleCommands();
        }

        /// <summary>
        ///     Read the configuration
        /// </summary>
        /// <param name="configSource"></param>
        public virtual void Configuration(IConfigSource configSource)
        {
            IConfig startupConfig = m_config.Configs["Startup"];

            int stpMinThreads = 15;
            int stpMaxThreads = 300;

            if (startupConfig != null)
            {
                m_defaultDataPath = startupConfig.GetString("DataDirectory", Constants.DEFAULT_DATA_DIR);
                if (m_defaultDataPath == "")
                    m_defaultDataPath = Constants.DEFAULT_DATA_DIR;
                
                m_startupCommandsFile = startupConfig.GetString("startup_console_commands_file", "startup_commands.txt");
                m_shutdownCommandsFile = startupConfig.GetString("shutdown_console_commands_file",
                                                                 "shutdown_commands.txt");

                m_TimerScriptFileName = startupConfig.GetString("timer_Script", "disabled");
                m_TimerScriptTime = startupConfig.GetInt("timer_time", m_TimerScriptTime);

                string pidFile = startupConfig.GetString("PIDFile", String.Empty);
                if (pidFile != String.Empty)
                    CreatePIDFile(pidFile);
            }

            IConfig SystemConfig = m_config.Configs["System"];
            if (SystemConfig != null)
            {
                string asyncCallMethodStr = SystemConfig.GetString("AsyncCallMethod", String.Empty);
                FireAndForgetMethod asyncCallMethod;
                if (!String.IsNullOrEmpty(asyncCallMethodStr) &&
                    Utils.EnumTryParse(asyncCallMethodStr, out asyncCallMethod))
                    Util.FireAndForgetMethod = asyncCallMethod;

                stpMinThreads = SystemConfig.GetInt("MinPoolThreads", stpMinThreads);
                stpMaxThreads = SystemConfig.GetInt("MaxPoolThreads", stpMaxThreads);

                if (stpMinThreads < 2)
                    stpMinThreads = 2;
                if (stpMaxThreads < 2)
                    stpMaxThreads = 2;
                if (stpMinThreads > stpMaxThreads)
                    stpMinThreads = stpMaxThreads;
                
            }


            if (Util.FireAndForgetMethod == FireAndForgetMethod.SmartThreadPool)
                Util.InitThreadPool(stpMinThreads, stpMaxThreads);

            //Set up console forcefully
            Universe.Services.BaseService consoleService = new Universe.Services.BaseService();
            consoleService.PreStartup(this);

            //Fix the default prompt
            if (MainConsole.Instance != null)
            {
                MainConsole.Instance.DefaultPrompt = m_consolePrompt;
                MainConsole.Instance.Info(string.Format("[MINUniverse]: STARTING MIN Universe ({0})...",
                                                        (IntPtr.Size == 4 ? "x86" : "x64")));
                MainConsole.Instance.Info("[MINUniverse]: Version: " + Version + "\n");
            }
        }

        /// <summary>
        ///     Performs initialization of the application, such as loading the HTTP server and modules
        /// </summary>
        public virtual void Startup()
        {
            MainConsole.Instance.Info("[MINUniverse]: Startup completed in " +
                                      (DateTime.Now - this.StartupTime).TotalSeconds);
        }

        public virtual ISimulationBase Copy()
        {
            return new MinimalSimulationBase(m_consolePrompt, m_dataPlugins, m_servicePlugins);
        }

        /// <summary>
        ///     Run the console now that we are all done with startup
        /// </summary>
        public virtual void Run()
        {
            //Start the prompt
            if (MainConsole.Instance != null)
                MainConsole.Instance.ReadConsole();
        }

        /// <summary>
        ///     Get an HTTPServer on the given port. It will create one if one does not exist
        /// </summary>
        /// <param name="port"></param>
        /// <returns></returns>
        public IHttpServer GetHttpServer(uint port)
        {
            if ((port == m_Port || port == 0) && HttpServer != null)
                return HttpServer;

            bool useHTTPS = m_config.Configs["Network"].GetBoolean("use_https", false);
            IHttpServer server;
            if (m_Servers.TryGetValue(port, out server) && server.Secure == useHTTPS)
                return server;

            uint threadCount = m_config.Configs["Network"].GetUInt("HttpThreadCount", 5);

            // find out where we live
            string hostName;

            // been here before?
            if (Utilities.HostName == "")
            {
                hostName = m_config.Configs ["Network"].GetString ("HostName", "0.0.0.0");

                if ((hostName == "") || (hostName == "0.0.0.0"))
                {
                    MainConsole.Instance.Info ("[Network]: Retrieving the external IP address");
                    hostName = "http" + (useHTTPS ? "s" : "") + "://" + Utilities.GetExternalIp ();
                }
            
                //Clean it up a bit
                if (hostName.StartsWith ("http://") || hostName.StartsWith ("https://"))
                    hostName = hostName.Replace ("https://", "").Replace ("http://", "");
                if (hostName.EndsWith ("/"))
                    hostName = hostName.Remove (hostName.Length - 1, 1);
                
                // save this for posterity in case it is needed
                MainConsole.Instance.Info ("[Network]: Network IP address has been set to " + hostName);
                Utilities.HostName = hostName;
            } else
                hostName = Utilities.HostName;

            server = new BaseHttpServer(port, hostName, useHTTPS, threadCount);

            try
            {
                server.Start();
            }
            catch (Exception)
            {
                //Remove the server from the list
                m_Servers.Remove(port);
                //Then pass the exception upwards
                throw;
            }
            if (m_Servers.Count == 0)
                MainServer.Instance = server;
            return (m_Servers[port] = server);
        }

        public virtual void InitializeModules()
        {
            LocalDataService lds = new LocalDataService();
            lds.Initialize(ConfigSource, ApplicationRegistry, m_dataPlugins);

            List<dynamic> modules = new List<dynamic>();
            foreach (Type t in m_servicePlugins)
            {
                var mods = UniverseModuleLoader.PickupModules(t);
                modules.AddRange(mods);
            }

            foreach (dynamic service in modules)
            {
                if (!(service is IService)) continue;
                ((IService)service).Initialize(ConfigSource, ApplicationRegistry);
            }
            foreach (dynamic service in modules)
            {
                if (!(service is IService)) continue;
                ((IService) service).Start(ConfigSource, ApplicationRegistry);
            }
            foreach (dynamic service in modules)
            {
                if (!(service is IService)) continue;
                ((IService)service).FinishedStartup();
            }
        }

        /// <summary>
        ///     Close all the Application Plugins
        /// </summary>
        public virtual void CloseModules()
        {
        }

        /// <summary>
        ///     Run the commands given now that startup is complete
        /// </summary>
        public void RunStartupCommands()
        {
            //Draw the file on the console
            PrintFileToConsole("startuplogo.txt");
            //Run Startup Commands
            if (!String.IsNullOrEmpty(m_startupCommandsFile))
                RunCommandScript(m_startupCommandsFile);

            // Start timer script (run a script every xx seconds)
            if (m_TimerScriptFileName != "disabled")
            {
                Timer newtimername = new Timer {Enabled = true, Interval = m_TimerScriptTime*60*1000};
                newtimername.Elapsed += RunAutoTimerScript;
            }
        }

        /// <summary>
        ///     Opens a file and uses it as input to the console command parser.
        /// </summary>
        /// <param name="fileName">name of file to use as input to the console</param>
        private void PrintFileToConsole(string fileName)
        {
            if (File.Exists(fileName))
            {
                StreamReader readFile = File.OpenText(fileName);
                string currentLine;
                while ((currentLine = readFile.ReadLine()) != null)
                {
                    MainConsole.Instance.Info("[!]" + currentLine);
                }
            }
        }

        /// <summary>
        ///     Timer to run a specific text file as console commands.
        ///     Configured in in the main .ini file
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void RunAutoTimerScript(object sender, EventArgs e)
        {
            RunCommandScript(m_TimerScriptFileName);
        }

        #region Console Commands

        /// <summary>
        ///     Register standard set of region console commands
        /// </summary>
        public virtual void RegisterConsoleCommands()
        {
            if (MainConsole.Instance == null)
                return;
            MainConsole.Instance.Commands.AddCommand("quit", 
                                                     "quit", 
                                                     "Quit the application", 
                                                     HandleQuit, false, true);
            
            MainConsole.Instance.Commands.AddCommand("shutdown",
                                                     "shutdown", 
                                                     "Quit the application", 
                                                     HandleQuit, false, true);
            
            MainConsole.Instance.Commands.AddCommand("show info",
                                                     "show info",
                                                     "Show server information (e.g. startup path)", 
                                                     HandleShowInfo, false, true);
            
            MainConsole.Instance.Commands.AddCommand("show version",
                                                     "show version", 
                                                     "Show server version",
                                                     HandleShowVersion, false, true);
            
            MainConsole.Instance.Commands.AddCommand("reload config",
                                                     "reload config", 
                                                     "Reloads .ini file configuration",
                                                     HandleConfigRefresh, false, true);
            
            MainConsole.Instance.Commands.AddCommand("set timer script interval",
                                                     "set timer script interval",
                                                     "Set the interval for the timer script (in minutes).",
                                                     HandleTimerScriptTime, false, true);
            
            MainConsole.Instance.Commands.AddCommand("force GC",
                                                     "force GC", 
                                                     "Forces garbage collection.", 
                                                     HandleForceGC, false, true);
            
            MainConsole.Instance.Commands.AddCommand("run configurator",
                                                     "run configurator", 
                                                     "Runs Universe.Configurator.",
                                                     runConfig, false, true);
        }

        private void HandleQuit(IScene scene, string[] args)
        {
            var ok = MainConsole.Instance.Prompt ("[CONSOLE]: Shutdown the simulator. Are you sure? (yes/no)", "no").ToLower();
            if (ok.StartsWith("y"))
                Shutdown(true);
        }

        /// <summary>
        ///     Run an optional startup list of commands
        /// </summary>
        /// <param name="fileName"></param>
        public virtual void RunCommandScript(string fileName)
        {
            if (File.Exists(fileName))
            {
                MainConsole.Instance.Info("[COMMANDFILE]: Running " + fileName);
                List<string> commands = new List<string>();
                using (StreamReader readFile = File.OpenText(fileName))
                {
                    string currentCommand;
                    while ((currentCommand = readFile.ReadLine()) != null)
                    {
                        if ( (currentCommand != String.Empty) &&
                            (!currentCommand.StartsWith(";")) )
                        {
                            commands.Add(currentCommand);
                        }
                    }
                }
                foreach (string currentCommand in commands)
                {
                    MainConsole.Instance.Info("[COMMANDFILE]: Running '" + currentCommand + "'");
                    MainConsole.Instance.RunCommand(currentCommand);
                }
            }
        }

        public virtual void HandleForceGC(IScene scene, string[] cmd)
        {
            GC.Collect();
            MainConsole.Instance.Warn("Garbage collection finished");
        }

        public virtual void runConfig(IScene scene, string[] cmd)
        {
            BaseApplication.Configure(true);
        }

        public virtual void HandleTimerScriptTime(IScene scene, string[] cmd)
        {
            if (cmd.Length != 5)
            {
                MainConsole.Instance.Warn("[CONSOLE]: Timer Interval command did not have enough parameters.");
                return;
            }
            MainConsole.Instance.Warn("[CONSOLE]: Set Timer Interval to " + cmd[4]);
            m_TimerScriptTime = int.Parse(cmd[4]);
            m_TimerScriptTimer.Enabled = false;
            m_TimerScriptTimer.Interval = m_TimerScriptTime*60*1000;
            m_TimerScriptTimer.Enabled = true;
        }

        public virtual void HandleConfigRefresh(IScene scene, string[] cmd)
        {
            //Rebuild the configuration
            m_config = m_configurationLoader.LoadConfigSettings(m_original_config);

            string hostName =
                m_config.Configs["Network"].GetString("HostName", "http://127.0.0.1");
            //Clean it up a bit
            // these are doing nothing??
            hostName.Replace("http://", "");
            hostName.Replace("https://", "");
            if (hostName.EndsWith("/"))
                hostName = hostName.Remove(hostName.Length - 1, 1);
            foreach (IHttpServer server in m_Servers.Values)
            {
                server.HostName = hostName;
            }
            MainConsole.Instance.Info("Finished reloading configuration.");
        }

        public virtual void HandleShowInfo(IScene scene, string[] cmd)
        {
            MainConsole.Instance.Info("Version: " + m_version);
            MainConsole.Instance.Info("Startup directory: " + Environment.CurrentDirectory);
        }

        public virtual void HandleShowVersion(IScene scene, string[] cmd)
        {
            MainConsole.Instance.Info(
                String.Format(
                    "Version: {0}", m_version));
        }

        #endregion

        /// <summary>
        ///     Should be overridden and referenced by descendents if they need to perform extra shutdown processing
        ///     Performs any last-minute sanity checking and shuts down the region server
        /// </summary>
        public virtual void Shutdown(bool close)
        {
            try
            {
                try
                {
                    RemovePIDFile();
                    if (m_shutdownCommandsFile != String.Empty)
                    {
                        RunCommandScript(m_shutdownCommandsFile);
                    }
                }
                catch
                {
                    //It doesn't matter, just shut down
                }
                try
                {
                    //Close out all the modules
                    CloseModules();
                }
                catch
                {
                    //Just shut down already
                }
                try
                {
                    //Close the thread pool
                    Util.CloseThreadPool();
                }
                catch
                {
                    //Just shut down already
                }
                try
                {
                    //Stop the HTTP server(s)
                    foreach (IHttpServer server in m_Servers.Values)
                    {
                        server.Stop();
                    }
                }
                catch
                {
                    //Again, just shut down
                }

                if (close)
                    MainConsole.Instance.Info("[SHUTDOWN]: Terminating");

                MainConsole.Instance.Info("[SHUTDOWN]: Shutdown processing on main thread complete. " +
                                          (close ? " Exiting..." : ""));

                if (close)
                    Environment.Exit(0);
            }
            catch
            {
            }
        }

        /// <summary>
        ///     Write the PID file to the hard drive
        /// </summary>
        /// <param name="path"></param>
        protected void CreatePIDFile(string path)
        {
            try
            {
                string pidstring = System.Diagnostics.Process.GetCurrentProcess().Id.ToString();
                FileStream fs = File.Create(path);
                System.Text.ASCIIEncoding enc = new System.Text.ASCIIEncoding();
                Byte[] buf = enc.GetBytes(pidstring);
                fs.Write(buf, 0, buf.Length);
                fs.Close();
                m_pidFile = path;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        ///     Delete the PID file now that we are done running
        /// </summary>
        protected void RemovePIDFile()
        {
            if (m_pidFile != String.Empty)
            {
                try
                {
                    File.Delete(m_pidFile);
                    m_pidFile = String.Empty;
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
