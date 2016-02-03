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

using System.Collections.Generic;
using System.Linq;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using Universe.Framework.ConsoleFramework;
using Universe.Framework.DatabaseInterfaces;
using Universe.Framework.Modules;
using Universe.Framework.SceneInfo;
using Universe.Framework.Serialization;
using Universe.Framework.Services;
using Universe.Framework.Utilities;

namespace Universe.Modules.Estate
{
    public class EstateInitializer : ISharedRegionStartupModule, IUniverseBackupModule
    {
        protected IRegistryCore m_registry;

        public void Initialize(IScene scene, IConfigSource source, ISimulationBase simBase)
        {
            scene.StackModuleInterface<IUniverseBackupModule>(this);
            m_registry = simBase.ApplicationRegistry;
        }

        public void PostInitialize(IScene scene, IConfigSource source, ISimulationBase simBase)
        {
        }

        public void FinishStartup(IScene scene, IConfigSource source, ISimulationBase simBase)
        {
            IEstateConnector EstateConnector = Framework.Utilities.DataManager.RequestPlugin<IEstateConnector>();

            if (EstateConnector != null)
            {
                EstateSettings ES = EstateConnector.GetEstateSettings(scene.RegionInfo.RegionID);
                if (ES == null)
                {
                    //Could not locate the estate service, wait until it can find it
                    MainConsole.Instance.Warn(
                        "We could not find the estate service for this region. Please make sure that your URLs are correct in grid mode.");
                    while (true)
                    {
                        MainConsole.Instance.Prompt("Press enter to try again.");
                        if ((ES = EstateConnector.GetEstateSettings(scene.RegionInfo.RegionID)) == null ||
                            ES.EstateID == 0)
                        {
                            ES = CreateEstateInfo(scene);
                            break;
                        }
                        if (ES != null)
                            break;
                    }
                }
                else if (ES.EstateID == 0)
                {
                    //This region does not belong to an estate, make a new one or join and existing one
                    MainConsole.Instance.Warn("[EstateInitializer]: Your region '" + scene.RegionInfo.RegionName +
                        "' is not part of an estate.");

                    ES = CreateEstateInfo(scene);
                }
                scene.RegionInfo.EstateSettings = ES;
            }
        }

        public void PostFinishStartup(IScene scene, IConfigSource source, ISimulationBase simBase)
        {
        }

        public void StartupComplete()
        {
            if (MainConsole.Instance != null)
            {
                MainConsole.Instance.Commands.AddCommand(
                    "change estate",
                    "change estate",
                    "change info about the estate for the given region",
                    ChangeEstate, true, false);
            }
        }

        public void Close(IScene scene)
        {
        }

        public void DeleteRegion(IScene scene)
        {
        }

        /// <summary>
        /// Links a region to a system estate.
        /// </summary>
        /// <returns>The system estate.</returns>
        /// <param name="regionID">Region ID.</param>
        /// <param name="estateID">Estate ID.</param>
        EstateSettings LinkSystemEstate(UUID regionID, int estateID)
        {
            // link region to a system estate > Mainland / Governor  or System / RealEstateOwner
            IEstateConnector estateConnector = Framework.Utilities.DataManager.RequestPlugin<IEstateConnector>();
            ISystemEstateService sysEstates = m_registry.RequestModuleInterface<ISystemEstateService>();
            EstateSettings ES;
            string estateName = sysEstates.GetSystemEstateName(estateID);

            // try & link region 
            if (estateConnector.LinkRegion(regionID, estateID))
            {
                ES = estateConnector.GetEstateSettings(regionID);     // refresh to check linking
                if ((ES == null) || (ES.EstateID == 0))
                {
                    MainConsole.Instance.Warn("An error was encountered linking the region to '" + estateName + "'!\n" +
                        "Possibly a problem with the server connection, please link this region later.");
                    return null;
                }
                MainConsole.Instance.Warn("Successfully joined '" + estateName + "'!");
                return ES;
            }

            MainConsole.Instance.Warn("Joining '" + estateName + "' failed. Please link this region later.");
            return null;
        }

        /// <summary>
        /// Creates the estate info for a region.
        /// </summary>
        /// <returns>The estate info.</returns>
        /// <param name="scene">Scene.</param>
        EstateSettings CreateEstateInfo(IScene scene)
        {
            // check for regionType to determine if this is 'Mainland' or an 'Estate'
            string regType = scene.RegionInfo.RegionType.ToLower();
            if (regType.StartsWith("m"))
            {
                return LinkSystemEstate(scene.RegionInfo.RegionID, Constants.MainlandEstateID);
            }

            // we are linking to a user estate
            IEstateConnector estateConnector = Framework.Utilities.DataManager.RequestPlugin<IEstateConnector>();
            ISystemAccountService sysAccounts = m_registry.RequestModuleInterface<ISystemAccountService>();

            string sysEstateOwnerName;
            var sysAccount = scene.UserAccountService.GetUserAccount(scene.RegionInfo.AllScopeIDs, sysAccounts.SystemEstateOwnerUUID);

            if (sysAccount == null)
                sysEstateOwnerName = sysAccounts.SystemEstateOwnerName;
            else
                sysEstateOwnerName = sysAccount.Name;

            // This is an 'Estate' so get some details....
            var LastEstateOwner = sysEstateOwnerName;
            string LastEstateName;

            while (true)
            {
                UserAccount account;
                string estateOwner;

                estateOwner = MainConsole.Instance.Prompt("Estate owner name (" + sysEstateOwnerName + "/User Name)", LastEstateOwner);

                // we have a prospective estate owner...
                List<EstateSettings> ownerEstates = null;
                account = scene.UserAccountService.GetUserAccount(scene.RegionInfo.AllScopeIDs, estateOwner);
                if (account != null)
                {
                    // we have a user account...
                    LastEstateOwner = account.Name;

                    ownerEstates = estateConnector.GetEstates(account.PrincipalID);
                }

                if (account == null || ownerEstates == null || ownerEstates.Count == 0)
                {
                    if (account == null)
                        MainConsole.Instance.Warn("[Estate]: Unable to locate the user " + estateOwner);
                    else
                        MainConsole.Instance.WarnFormat("[Estate]: The user, {0}, has no estates currently.", account.Name);

                    string joinSystemland = MainConsole.Instance.Prompt(
                        "Do you want to 'park' the region with the system owner/estate? (yes/no)", "yes");
                    if (joinSystemland.ToLower().StartsWith("y"))                      // joining 'joinSystemland'
                        return LinkSystemEstate(scene.RegionInfo.RegionID, Constants.SystemEstateID);

                    continue;
                }

                if (ownerEstates.Count > 1)
                {
                    MainConsole.Instance.InfoFormat("[Estate]: User {0} has {1} estates currently. {2}",
                        account.Name, ownerEstates.Count, "These estates are the following:");
                    foreach (EstateSettings t in ownerEstates)
                        MainConsole.Instance.CleanInfo("         " + t.EstateName);

                    LastEstateName = ownerEstates[0].EstateName;

                    List<string> responses = ownerEstates.Select(settings => settings.EstateName).ToList();
                    responses.Add("Cancel");

                    do
                    {
                        //TODO: This could be a problem if we have a lot of estates
                        string response = MainConsole.Instance.Prompt("Estate name to join", LastEstateName, responses);
                        if (response == "None" || response == "Cancel")
                        {
                            LastEstateName = "";
                            break;
                        }
                        LastEstateName = response;
                    } while (LastEstateName == "");
                    if (LastEstateName == "")
                        continue;
                }
                else
                    LastEstateName = ownerEstates[0].EstateName;

                // we should have a user account and estate name by now
                int estateID = estateConnector.GetEstate(account.PrincipalID, LastEstateName);
                if (estateID == 0)
                {
                    MainConsole.Instance.Warn("[Estate]: The name you have entered matches no known estate. Please try again");
                    continue;
                }

                // link up the region
                EstateSettings ES;
                UUID oldOwnerID = scene.RegionInfo.EstateSettings.EstateOwner;

                if (!estateConnector.LinkRegion(scene.RegionInfo.RegionID, estateID))
                {
                    MainConsole.Instance.WarnFormat("[Estate]: Joining the {0} estate failed. Please try again.", LastEstateName);
                    continue;
                }

                // make sure that the region is fully set up
                if ((ES = estateConnector.GetEstateSettings(scene.RegionInfo.RegionID)) == null || ES.EstateID == 0)
                {
                    MainConsole.Instance.Warn("[Estate]: Unable to verify region update (possible server connection error), please try again.");
                    continue;
                }

                // Linking was successful, change any previously owned parcels to the new owner 
                IParcelManagementModule parcelManagement = scene.RequestModuleInterface<IParcelManagementModule>();
                if (parcelManagement != null)
                    parcelManagement.ReclaimParcels(oldOwnerID, ES.EstateOwner);

                MainConsole.Instance.InfoFormat("[Estate]: Successfully joined the {0} estate!", LastEstateName);
                return ES;
            }
        }

        /// <summary>
        /// Changes the region estate.
        /// </summary>
        /// <param name="scene">Scene.</param>
        /// <param name="cmd">Cmd.</param>
        protected void ChangeEstate(IScene scene, string[] cmd)
        {
            IEstateConnector EstateConnector = Framework.Utilities.DataManager.RequestPlugin<IEstateConnector>();
            if (EstateConnector != null)
            {
                // a bit of info re 'Mainland'
                string regType = scene.RegionInfo.RegionType.ToLower();
                if (regType.StartsWith("m"))
                {
                    if (scene.RegionInfo.EstateSettings.EstateID == Constants.MainlandEstateID)
                    {
                        MainConsole.Instance.Info("[Estate]: This region is already part of the Mainland estate");
                        return;
                    }

                    // link this region to the mainland
                    MainConsole.Instance.Info("[Estate]: Mainland type regions must be part of the Mainland estate");
                    LinkSystemEstate(scene.RegionInfo.RegionID, Constants.MainlandEstateID);
                    return;
                }

                string removeFromEstate =
                    MainConsole.Instance.Prompt(
                        "Are you sure you want to change the estate for region '" + scene.RegionInfo.RegionName + "'? (yes/no)",
                        "yes");

                if (removeFromEstate == "yes")
                {
                    if (!EstateConnector.DelinkRegion(scene.RegionInfo.RegionID))
                    {
                        MainConsole.Instance.Warn("[Estate]: Unable to remove this region from the current estate.");
                        return;
                    }
                    // need to get new estate and re-link the region
                    scene.RegionInfo.EstateSettings = CreateEstateInfo(scene);
                }
                else
                    MainConsole.Instance.Warn("[Estate]: No action has been taken.");
            }
        }

        public bool IsArchiving
        {
            get { return false; }
        }

        /// <summary>
        /// Saves the estate settings to an archive.
        /// </summary>
        /// <param name="writer">Writer.</param>
        /// <param name="scene">Scene.</param>
        public void SaveModuleToArchive(TarArchiveWriter writer, IScene scene)
        {
            MainConsole.Instance.Debug("[Archive]: Writing estates to archive");

            EstateSettings settings = scene.RegionInfo.EstateSettings;
            if (settings == null)
                return;
            writer.WriteDir("estatesettings");
            writer.WriteFile("estatesettings/" + scene.RegionInfo.RegionName,
                             OSDParser.SerializeLLSDBinary(settings.ToOSD()));

            MainConsole.Instance.Debug("[Archive]: Finished writing estates to archive");
            MainConsole.Instance.Debug("[Archive]: Writing region info to archive");

            writer.WriteDir("regioninfo");
            RegionInfo regionInfo = scene.RegionInfo;

            writer.WriteFile("regioninfo/" + scene.RegionInfo.RegionName,
                             OSDParser.SerializeLLSDBinary(regionInfo.PackRegionInfoData()));

            MainConsole.Instance.Debug("[Archive]: Finished writing region info to archive");
        }

        /// <summary>
        /// Loads the estate settings from an archive.
        /// </summary>
        /// <param name="data">Data.</param>
        /// <param name="filePath">File path.</param>
        /// <param name="type">Type.</param>
        /// <param name="scene">Scene.</param>
        public void LoadModuleFromArchive(byte[] data, string filePath, TarArchiveReader.TarEntryType type, IScene scene)
        {
            if (filePath.StartsWith("estatesettings/"))
            {
                EstateSettings settings = new EstateSettings();
                settings.FromOSD((OSDMap)OSDParser.DeserializeLLSDBinary(data));
                scene.RegionInfo.EstateSettings = settings;
            }
            else if (filePath.StartsWith("regioninfo/"))
            {
                string m_merge =
                    MainConsole.Instance.Prompt(
                        "Should we load the region information from the archive (region name, region position, etc)?",
                        "false");
                RegionInfo settings = new RegionInfo();
                settings.UnpackRegionInfoData((OSDMap)OSDParser.DeserializeLLSDBinary(data));
                if (m_merge == "false")
                {
                    //Still load the region settings though
                    scene.RegionInfo.RegionSettings = settings.RegionSettings;
                    return;
                }
                settings.RegionSettings = scene.RegionInfo.RegionSettings;
                settings.EstateSettings = scene.RegionInfo.EstateSettings;
                scene.RegionInfo = settings;
            }
        }

        public void BeginLoadModuleFromArchive(IScene scene)
        {
        }

        public void EndLoadModuleFromArchive(IScene scene)
        {
        }
    }
}