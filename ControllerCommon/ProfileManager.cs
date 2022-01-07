﻿using Force.Crc32;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using static ControllerCommon.Utils;

namespace ControllerCommon
{
    public class ProfileManager
    {
        private Dictionary<bool, uint[]> CRCs = new Dictionary<bool, uint[]>()
        {
            { false, new uint[]{ 0xcd4906cc, 0xcd4906cc, 0xcd4906cc, 0xcd4906cc, 0xcd4906cc } },
            { true, new uint[]{ 0x1e9df650, 0x1e9df650, 0x1e9df650, 0x1e9df650, 0x1e9df650 } },
        };

        public Dictionary<string, Profile> profiles = new Dictionary<string, Profile>();
        public FileSystemWatcher profileWatcher { get; set; }

        public event DeletedEventHandler Deleted;
        public delegate void DeletedEventHandler(Profile profile);
        public event UpdatedEventHandler Updated;
        public delegate void UpdatedEventHandler(Profile profile);

        public HIDmode HIDmode;
        public PipeClient PipeClient;

        private readonly ILogger logger;
        private string path;

        public ProfileManager(string path, ILogger logger, PipeClient PipeClient = null)
        {
            this.logger = logger;
            this.path = path;
            this.PipeClient = PipeClient;

            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            // monitor profile file deletions
            profileWatcher = new FileSystemWatcher()
            {
                Path = path,
                EnableRaisingEvents = true,
                IncludeSubdirectories = true,
                Filter = "*.json",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size
            };
        }

        public void Start(string filter = "*.json")
        {
            profileWatcher.Deleted += ProfileDeleted;

            // process existing profiles
            string[] fileEntries = Directory.GetFiles(path, filter, SearchOption.AllDirectories);
            foreach (string fileName in fileEntries)
                ProcessProfile(fileName);

            // create default profile if missing
            if (GetDefault() == null)
                SetDefault();
        }

        public void Stop()
        {
            profileWatcher.Deleted -= ProfileDeleted;
            profileWatcher.Dispose();
        }

        public void UpdateHID(HIDmode HIDmode)
        {
            this.HIDmode = HIDmode;
        }

        private void ProfileDeleted(object sender, FileSystemEventArgs e)
        {
            string ProfileName = e.Name.Replace(".json", "");

            if (!profiles.ContainsKey(ProfileName))
                return;

            switch (ProfileName)
            {
                // prevent default profile from being deleted
                case "Default":
                    SetDefault();
                    break;
                default:
                    Profile profile = profiles[ProfileName];
                    DeleteProfile(profile);
                    break;
            }
        }

        public void SetDefault()
        {
            Profile def = new Profile("Default", "");
            profiles["Default"] = def;
            SerializeProfile(def);
        }

        public Profile GetDefault()
        {
            if (profiles.ContainsKey("Default"))
                return profiles["Default"];
            return null;
        }

        private void ProcessProfile(string fileName)
        {
            Profile profile = null;
            try
            {
                string outputraw = File.ReadAllText(fileName);
                profile = JsonSerializer.Deserialize<Profile>(outputraw);
                profile.fullpath = profile.path;
            }
            catch (Exception ex)
            {
                logger.LogError("Could not parse {0}. {1}", fileName, ex.Message);
            }

            // failed to parse
            if (profile == null || profile.name == null || profile.path == null)
            {
                logger.LogError("Could not parse {0}.", fileName);
                return;
            }

            if (profile.name == "Default")
                profile.IsDefault = true;

            UpdateProfile(profile);
        }

        public void DeleteProfile(Profile profile)
        {
            string settingsPath = Path.Combine(path, $"{profile.name}.json");

            if (profiles.ContainsKey(profile.name))
            {
                UnregisterApplication(profile);
                profiles.Remove(profile.name);
                Deleted?.Invoke(profile);
                logger.LogInformation("Deleted profile {0}", settingsPath);
            }

            File.Delete(settingsPath);
        }

        public void SerializeProfile(Profile profile)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(profile, options);

            string settingsPath = Path.Combine(path, $"{profile.name}.json");
            File.WriteAllText(settingsPath, jsonString);
        }

        private ProfileErrorCode SanitizeProfile(Profile profile)
        {
            string processpath = Path.GetDirectoryName(profile.fullpath);

            if (!Directory.Exists(processpath))
                return ProfileErrorCode.MissingPath;
            else if (!File.Exists(profile.fullpath))
                return ProfileErrorCode.MissingExecutable;
            else if (!Utils.IsDirectoryWritable(processpath))
                return ProfileErrorCode.MissingPermission;

            return ProfileErrorCode.None;
        }

        public void UpdateProfile(Profile profile)
        {
            // update database
            profiles[profile.name] = profile;

            // refresh error code
            profile.error = SanitizeProfile(profile);

            // update cloaking
            UpdateProfileCloaking(profile);

            // warn owner
            Updated?.Invoke(profile);

            if (profile.error != ProfileErrorCode.None && !profile.IsDefault)
            {
                logger.LogError("Profile {0} returned error code {1}", profile.name, profile.error);
                return;
            }

            // update wrapper
            UpdateProfileWrapper(profile);
        }

        public void UpdateProfileCloaking(Profile profile)
        {
            if (profile.whitelisted)
                RegisterApplication(profile);
            else
                UnregisterApplication(profile);
        }

        public void UpdateProfileWrapper(Profile profile)
        {
            // deploy xinput wrapper
            string XinputPlus = Properties.Resources.XInputPlus;

            string[] fullpaths = new string[] { profile.fullpath };

            // for testing purposes, this should not happen!
            if (profile.IsDefault)
            {
                fullpaths = new string[]
                {
                    @"C:\Windows\System32\cmd.exe",
                    @"C:\Windows\SysWOW64\cmd.exe"
                };
            }

            foreach (string fullpath in fullpaths)
            {
                string processpath = Path.GetDirectoryName(fullpath);
                string inipath = Path.Combine(processpath, "XInputPlus.ini");
                bool iniexist = File.Exists(inipath);

                // get binary type (x64, x86)
                BinaryType bt; GetBinaryType(fullpath, out bt);
                bool x64 = bt == BinaryType.SCS_64BIT_BINARY;

                if (profile.use_wrapper)
                    File.WriteAllText(inipath, XinputPlus);
                else if (iniexist)
                    File.Delete(inipath);

                for (int i = 0; i < 5; i++)
                {
                    string dllpath = Path.Combine(processpath, $"xinput1_{i + 1}.dll");
                    string backpath = Path.Combine(processpath, $"xinput1_{i + 1}.back");

                    // dll has a different naming format
                    if (i == 4)
                    {
                        dllpath = Path.Combine(processpath, $"xinput9_1_0.dll");
                        backpath = Path.Combine(processpath, $"xinput9_1_0.back");
                    }

                    bool dllexist = File.Exists(dllpath);
                    bool backexist = File.Exists(backpath);

                    byte[] data = new byte[] { 0 };

                    // check CRC32
                    if (dllexist) data = File.ReadAllBytes(dllpath);
                    var crc = Crc32Algorithm.Compute(data);
                    bool is_x360ce = CRCs[x64][i] == crc;

                    switch (i)
                    {
                        case 0:
                            data = x64 ? Properties.Resources.xinput1_11 : Properties.Resources.xinput1_1;
                            break;
                        case 1:
                            data = x64 ? Properties.Resources.xinput1_21 : Properties.Resources.xinput1_2;
                            break;
                        default:
                        case 2:
                            data = x64 ? Properties.Resources.xinput1_31 : Properties.Resources.xinput1_3;
                            break;
                        case 3:
                            data = x64 ? Properties.Resources.xinput1_41 : Properties.Resources.xinput1_4;
                            break;
                        case 4:
                            data = x64 ? Properties.Resources.xinput9_1_01 : Properties.Resources.xinput9_1_0;
                            break;
                    }

                    if (profile.use_wrapper)
                    {
                        if (!IsFileWritable(dllpath))
                            SetFileWritable(dllpath);

                        if (dllexist && is_x360ce)
                            continue; // skip to next file
                        else if (!dllexist)
                            File.WriteAllBytes(dllpath, data);
                        else if (dllexist && !is_x360ce)
                        {
                            // create backup of current dll
                            if (!backexist)
                                File.Move(dllpath, backpath);

                            // deploy wrapper
                            File.WriteAllBytes(dllpath, data);
                        }
                    }
                    else
                    {
                        // delete wrapper dll
                        if (dllexist && is_x360ce)
                            File.Delete(dllpath);

                        // restore backup is exists
                        if (backexist)
                            File.Move(backpath, dllpath);
                    }
                }
            }
        }

        public void UnregisterApplication(Profile profile)
        {
            PipeClient?.SendMessage(new PipeClientHidder
            {
                action = HidderAction.Unregister,
                path = profile.fullpath
            });
        }

        public void RegisterApplication(Profile profile)
        {
            PipeClient?.SendMessage(new PipeClientHidder
            {
                action = HidderAction.Register,
                path = profile.fullpath
            });
        }
    }
}