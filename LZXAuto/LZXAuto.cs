﻿using LZXAutoEngine;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Win32.TaskScheduler;


namespace LZXAuto
{
	class Program
	{
        private const string TaskScheduleName = "LZXAuto";

        private readonly static LZXAutoEngine.LZXAutoEngine compressorEngine = new LZXAutoEngine.LZXAutoEngine();

        static void Main(string[] args)
        {
            var currentProcess = Process.GetCurrentProcess();
            string currentProcessName = currentProcess.ProcessName;
            string currentProcessPath = currentProcess.MainModule.FileName;

            if (Process.GetProcesses().Count(p => p.ProcessName == currentProcessName) > 1)
            {
                compressorEngine.Logger.Log("Another instance is already running. Exiting...", 2, LogLevel.General);
                return;
            }

            AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
            Console.CancelKeyPress += new ConsoleCancelEventHandler(ConsoleTerminateHandler);

            string commandLine = string.Empty;
            if (args != null)
            {
                commandLine = string.Join(" ", args);
            }

            // Parse help option
            if (args.Length == 0 || args.Contains("/?") || args.Contains("/help"))
            {
                Console.WriteLine($@"
Automatically compress files to NTFS LZX compression with minimal disk write cycles.
                
Syntax: LZXAuto [/log:mode] [/resetDb] [/scheduleOn] [/scheduleOff] [/? | /help] [filePath]

Options:

/log: [None, General, Info, Debug] - log level. Default value: Info
None    - nothing is outputted
General - Session start / end timestamp, skipped folders
Info    - General + statistics about current session
Debug   - Info + information about every file

/resetDb - resets db. On next run, all files will be traversed by Compact command.

/scheduleOn - enables Task Scheduler entry to run LZXAuto when computer is idle for 10 minutes. Task runs daily.

/scheduleOff - disables Task Scheduler entry

/? or /help - displays this help screen

filePath - root path to start. All subdirectories will be traversed. Default is root of current drive, like c:\.

Description:
Windows 10 extended NTFS compression with LZX alghorithm. 
Files compressed with LZX can be opened like any other file because the uncompressing operation is transparent.
Compressing files with LZX is CPU intensive and thus is not being done automatically. When file is updated, it will be saved in uncompressed state.
To keep the files compressed, windows Compact command needs to be re-run. This can be done with Task Scheduler.

There is a catch with SSD drives though.
When Compact command is being run on file already LZX-compressed, it will not try to recompress it.
However, if file is not compressible (like .jpg image), Compact will try to recompress it every time, writing temp data to disk.

This is an issue on SSD drives, because of limited write cycles.
LZXAuto keeps record of file name and its last seen size. If the file has not changed since last LZXAuto run, it will be skipped. 
This saves SSD write cycles and also speeds up processing time, as on second run only newly updated / inserted files are processed.

If folder is found with NTFS compression enabled, after processing it will be marked as non-compressed. 
This is because LZX-compression does not use NTFS Compressed attribute.

Iterating through files is multithreaded, one file per CPU logical core.
For larger file accessibility, this command should be run with Adminstrator priviledges.

Typical use:
LZXAuto /scheduleOn c:\ 

Version number: {Assembly.GetEntryAssembly().GetName().Version}
");

                return;
            }

            // Parse resetDb option
            if (args.Contains("/resetDb", StringComparer.InvariantCultureIgnoreCase))
            {
                compressorEngine.ResetDb();
                return;
            }

            compressorEngine.Logger.LogLevel = LogLevel.Info;


            // Parse log level option, like: /q:general
            if (!string.IsNullOrEmpty(commandLine))
            {
                Regex rx = new Regex(@"/log:(?<mode>(\s*\w+\s*[,]{0,1})*)(?![:/])", RegexOptions.IgnoreCase);
                var match = rx.Match(commandLine);
                if (match.Success)
                {
                    compressorEngine.Logger.LogLevel = LogLevel.None;

                    string modeStr = match.Groups?["mode"]?.Value;
                    string[] modeArr = modeStr.Replace(" ", string.Empty).Split(',');

                    foreach (string modeVal in modeArr.Where(a => !string.IsNullOrEmpty(a)))
                    {
                        if (!Enum.TryParse<LogLevel>(modeVal, true, out LogLevel logL))
                        {
                            Console.WriteLine($"Unrecognised log level value: {modeVal}");
                            return;
                        }

                        compressorEngine.Logger.LogLevel = logL;
                    }
                }
            }

            // Parse path option
            string commandLineRequestedPath = Path.GetPathRoot(System.Reflection.Assembly.GetEntryAssembly().Location);
            foreach (string arg in args)
            {
                Regex rx = new Regex(@"[a-z]:\\", RegexOptions.IgnoreCase);
                if (!string.IsNullOrEmpty(rx.Match(arg)?.Value))
                {
                    commandLineRequestedPath = arg;
                    if (!commandLineRequestedPath.EndsWith("\\"))
                    {
                        commandLineRequestedPath += "\\";
                    }
                }
            }

            // Parse scheduleOn option
            if (args.Contains("/scheduleOn", StringComparer.InvariantCultureIgnoreCase))
            {
                string requestedPath = commandLineRequestedPath;
                if (string.IsNullOrEmpty(requestedPath))
                    requestedPath = Path.GetPathRoot(currentProcessPath);

                string workingDirectory = Path.GetDirectoryName(currentProcessPath);

                TaskService ts = null;
                try {
                    ts = new TaskService();
                    TaskDefinition td = ts.NewTask();
                    td.RegistrationInfo.Description = "Automatically compress files to NTFS LZX compression with minimal disk write cycles";
                    td.Settings.AllowDemandStart = true;
                    td.Settings.ExecutionTimeLimit = new TimeSpan (8, 0, 0);
                    td.Triggers.Add(new DailyTrigger { DaysInterval = 7 });
                    td.Actions.Add(new ExecAction(currentProcessPath, requestedPath, workingDirectory));
                    ts.RootFolder.RegisterTaskDefinition(TaskScheduleName, td, TaskCreation.CreateOrUpdate, "SYSTEM", null, TaskLogonType.ServiceAccount);
                    Console.WriteLine("Schedule initialized");
                } catch (Exception ex) {
                    Console.WriteLine("Schedule initialization failed due to " + ex.Message);
                } finally {
                    if (ts != null) ts.Dispose();
                }
                return;
            }

            // Parse scheduleOff option
            if (args.Contains("/scheduleOff", StringComparer.InvariantCultureIgnoreCase))
            {
                TaskService ts = null;
                try {
                    ts = new TaskService();
                    ts.RootFolder.DeleteTask(TaskScheduleName);
                } catch (Exception ex) {
                    Console.WriteLine("Schedule removal failed due to " + ex.Message);
                } finally {
                    if (ts != null) ts.Dispose();
                }
                return;
            }

            string[] skipFileExtensions;
            try
            {
                // Read config file
                var file = File.ReadAllText(@"LZXAutoConfig.json");
                dynamic j = JsonConvert.DeserializeObject(file);
                skipFileExtensions = j.skipFileExtensions.ToObject<string[]>();
            }
            catch (Exception ex)
            {
                compressorEngine.Logger.Log(ex, "Could not parse LZXAutoConfig.json");
                return;
            }

            compressorEngine.Process(commandLineRequestedPath, skipFileExtensions);
        }

        private static void CurrentDomain_ProcessExit(object sender, EventArgs e)
        {
            //compressorEngine.Cancel();
        }

        private static void ConsoleTerminateHandler(object sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = false;
            compressorEngine.Cancel();
        }
    }
}
