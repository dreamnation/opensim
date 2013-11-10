/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
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
using System.Reflection;
using System.Threading;
using Timer = System.Timers.Timer;

using log4net;
using Nini.Config;
using Mono.Addins;
using OpenMetaverse;

using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.OptionalModules.World.NPC
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "NPCModule")]
    public class NPCModule : INPCModule, ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        private Dictionary<UUID, NPCAvatar> m_avatars =
                new Dictionary<UUID, NPCAvatar>();

        public bool Enabled { get; private set; }

        private string npcGroupTitle;
        private string npcGroupTitleExcludeName;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["NPC"];

            Enabled = (config != null && config.GetBoolean("Enabled", false));
            if (Enabled) {
                npcGroupTitle = config.GetString ("NPCGroupTitle", "");
                npcGroupTitleExcludeName = config.GetString ("NPCGroupTitleExcludeName", npcGroupTitle);
                MainConsole.Instance.Commands.AddCommand ("npc", false, "npc", "npc [...|help|...]", "NPC commands", ConsoleCommands);
            }
        }

        public void AddRegion(Scene scene)
        {
            if (Enabled)
                scene.RegisterModuleInterface<INPCModule>(this);
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void PostInitialise()
        {
        }

        public void RemoveRegion(Scene scene)
        {
            scene.UnregisterModuleInterface<INPCModule>(this);
        }

        public void Close()
        {
        }

        public string Name
        {
            get { return "NPCModule"; }
        }

        public Type ReplaceableInterface { get { return null; } }

        public bool IsNPC(UUID agentId, Scene scene)
        {
            // FIXME: This implementation could not just use the
            // ScenePresence.PresenceType (and callers could inspect that
            // directly).
            ScenePresence sp = scene.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent)
                return false;

            lock (m_avatars)
                return m_avatars.ContainsKey(agentId);
        }

        public bool SetNPCAppearance(UUID agentId,
                AvatarAppearance appearance, Scene scene)
        {
            ScenePresence npc = scene.GetScenePresence(agentId);
            if (npc == null || npc.IsChildAgent)
                return false;

            lock (m_avatars)
                if (!m_avatars.ContainsKey(agentId))
                    return false;

            // Delete existing npc attachments
            if(scene.AttachmentsModule != null)
                scene.AttachmentsModule.DeleteAttachmentsFromScene(npc, false);

            // XXX: We can't just use IAvatarFactoryModule.SetAppearance() yet
            // since it doesn't transfer attachments
            AvatarAppearance npcAppearance = new AvatarAppearance(appearance,
                    true);
            npc.Appearance = npcAppearance;

            // Rez needed npc attachments
            if (scene.AttachmentsModule != null)
                scene.AttachmentsModule.RezAttachments(npc);

            IAvatarFactoryModule module =
                    scene.RequestModuleInterface<IAvatarFactoryModule>();
            module.SendAppearance(npc.UUID);

            return true;
        }

        public UUID CreateNPC(string firstname, string lastname,
                Vector3 position, UUID owner, bool senseAsAgent, Scene scene,
                AvatarAppearance appearance)
        {
            NPCAvatar npcAvatar = new NPCAvatar(firstname, lastname, position,
                    owner, senseAsAgent, scene);
            npcAvatar.CircuitCode = (uint)Util.RandomClass.Next(0,
                    int.MaxValue);

            m_log.DebugFormat(
                "[NPC MODULE]: Creating NPC {0} {1} {2}, owner={3}, senseAsAgent={4} at {5} in {6}",
                firstname, lastname, npcAvatar.AgentId, owner,
                senseAsAgent, position, scene.RegionInfo.RegionName);

            AgentCircuitData acd = new AgentCircuitData();
            acd.AgentID = npcAvatar.AgentId;
            acd.firstname = firstname;
            acd.lastname = lastname;
            acd.ServiceURLs = new Dictionary<string, object>();

            AvatarAppearance npcAppearance = new AvatarAppearance(appearance,
                    true);
            acd.Appearance = npcAppearance;

            /*
            for (int i = 0;
                    i < acd.Appearance.Texture.FaceTextures.Length; i++)
            {
                m_log.DebugFormat(
                        "[NPC MODULE]: NPC avatar {0} has texture id {1} : {2}",
                        acd.AgentID, i,
                        acd.Appearance.Texture.FaceTextures[i]);
            }
            */

            lock (m_avatars)
            {
                scene.AuthenticateHandler.AddNewCircuit(npcAvatar.CircuitCode,
                        acd);
                scene.AddNewAgent(npcAvatar, PresenceType.Npc);

                ScenePresence sp;
                if (scene.TryGetScenePresence(npcAvatar.AgentId, out sp))
                {
                    /*
                    m_log.DebugFormat(
                            "[NPC MODULE]: Successfully retrieved scene presence for NPC {0} {1}",
                            sp.Name, sp.UUID);
                    */

                    sp.CompleteMovement(npcAvatar, false);

                    /*
                     * If the parameters gave us a group title to force all NPCs to,
                     * and the firstname does not contain the exclude string
                     * and the lastname does not contain the exclude string,
                     * set the NPCs group title to the given string and send it to
                     * all connected viewers.
                     *
                     * This must be done before adding to m_avatars as once so added,
                     * the group title cannot be changed.
                     */
                    if ((npcGroupTitle != "") &&
                        !firstname.Contains (npcGroupTitleExcludeName) &&
                        !lastname.Contains (npcGroupTitleExcludeName)) {
                        sp.Grouptitle = npcGroupTitle;
                        sp.SendAvatarDataToAllAgents();
                    }

                    m_avatars.Add(npcAvatar.AgentId, npcAvatar);
                    m_log.DebugFormat("[NPC MODULE]: Created NPC {0} {1}", npcAvatar.AgentId, sp.Name);

                    return npcAvatar.AgentId;
                }
                else
                {
                    m_log.WarnFormat(
                        "[NPC MODULE]: Could not find scene presence for NPC {0} {1}",
                        sp.Name, sp.UUID);

                    return UUID.Zero;
                }
            }
        }

        public bool MoveToTarget(UUID agentID, Scene scene, Vector3 pos,
                bool noFly, bool landAtTarget, bool running)
        {
            lock (m_avatars)
            {
                if (m_avatars.ContainsKey(agentID))
                {
                    ScenePresence sp;
                    if (scene.TryGetScenePresence(agentID, out sp))
                    {
//                        m_log.DebugFormat(
//                                "[NPC MODULE]: Moving {0} to {1} in {2}, noFly {3}, landAtTarget {4}",
//                                sp.Name, pos, scene.RegionInfo.RegionName,
//                                noFly, landAtTarget);

                        sp.MoveToTarget(pos, noFly, landAtTarget);
                        sp.SetAlwaysRun = running;

                        return true;
                    }
                }
            }

            return false;
        }

        public bool StopMoveToTarget(UUID agentID, Scene scene)
        {
            lock (m_avatars)
            {
                if (m_avatars.ContainsKey(agentID))
                {
                    ScenePresence sp;
                    if (scene.TryGetScenePresence(agentID, out sp))
                    {
                        sp.Velocity = Vector3.Zero;
                        sp.ResetMoveToTarget();

                        return true;
                    }
                }
            }

            return false;
        }

        public bool Say(UUID agentID, Scene scene, string text)
        {
            return Say(agentID, scene, text, 0);
        }

        public bool Say(UUID agentID, Scene scene, string text, int channel)
        {
            lock (m_avatars)
            {
                if (m_avatars.ContainsKey(agentID))
                {
                    m_avatars[agentID].Say(channel, text);

                    return true;
                }
            }

            return false;
        }

        public bool Shout(UUID agentID, Scene scene, string text, int channel)
        {
            lock (m_avatars)
            {
                if (m_avatars.ContainsKey(agentID))
                {
                    m_avatars[agentID].Shout(channel, text);

                    return true;
                }
            }

            return false;
        }

        public bool Sit(UUID agentID, UUID partID, Scene scene)
        {
            lock (m_avatars)
            {
                if (m_avatars.ContainsKey(agentID))
                {
                    ScenePresence sp;
                    if (scene.TryGetScenePresence(agentID, out sp))
                    {
                        sp.HandleAgentRequestSit(m_avatars[agentID], agentID, partID, Vector3.Zero);

                        return true;
                    }
                }
            }

            return false;
        }

        public bool Whisper(UUID agentID, Scene scene, string text,
                int channel)
        {
            lock (m_avatars)
            {
                if (m_avatars.ContainsKey(agentID))
                {
                    m_avatars[agentID].Whisper(channel, text);

                    return true;
                }
            }

            return false;
        }

        public bool Stand(UUID agentID, Scene scene)
        {
            lock (m_avatars)
            {
                if (m_avatars.ContainsKey(agentID))
                {
                    ScenePresence sp;
                    if (scene.TryGetScenePresence(agentID, out sp))
                    {
                        sp.StandUp();

                        return true;
                    }
                }
            }

            return false;
        }

        public bool Touch(UUID agentID, UUID objectID)
        {
            lock (m_avatars)
            {
                if (m_avatars.ContainsKey(agentID))
                    return m_avatars[agentID].Touch(objectID);

                return false;
            }
        }

        public UUID GetOwner(UUID agentID)
        {
            lock (m_avatars)
            {
                NPCAvatar av;
                if (m_avatars.TryGetValue(agentID, out av))
                    return av.OwnerID;
            }

            return UUID.Zero;
        }

        public INPC GetNPC(UUID agentID, Scene scene)
        {
            lock (m_avatars)
            {
                if (m_avatars.ContainsKey(agentID))
                    return m_avatars[agentID];
                else
                    return null;
            }
        }

        public bool DeleteNPC(UUID agentID, IScene scene)
        {
            lock (m_avatars)
            {
                NPCAvatar av;
                if (m_avatars.TryGetValue(agentID, out av))
                {
                    /*
                    m_log.DebugFormat("[NPC MODULE]: Found {0} {1} to remove",
                            agentID, av.Name);
                    */

                    scene.CloseAgent(agentID, false);

                    m_avatars.Remove(agentID);

                    /*
                    m_log.DebugFormat("[NPC MODULE]: Removed NPC {0} {1}",
                            agentID, av.Name);
                    */
                    return true;
                }
            }
            /*
            m_log.DebugFormat("[NPC MODULE]: Could not find {0} to remove",
                    agentID);
            */
            return false;
        }

        public bool CheckPermissions(UUID npcID, UUID callerID)
        {
            lock (m_avatars)
            {
                NPCAvatar av;
                if (m_avatars.TryGetValue(npcID, out av))
                    return CheckPermissions(av, callerID);
                else
                    return false;
            }
        }

        /// <summary>
        /// Check if the caller has permission to manipulate the given NPC.
        /// </summary>
        /// <param name="av"></param>
        /// <param name="callerID"></param>
        /// <returns>true if they do, false if they don't.</returns>
        private bool CheckPermissions(NPCAvatar av, UUID callerID)
        {
            return callerID == UUID.Zero || av.OwnerID == UUID.Zero ||
                    av.OwnerID == callerID;
        }

        private void ConsoleCommands (string module, string[] args)
        {
            if (args.Length < 2) {
                m_log.Info ("[NPCModule]: missing command, try 'npc help'");
                return;
            }
            switch (args[1].ToLower ()) {
                case "help": {
                    m_log.Info ("[NPCModule]:   npc kill <part of name or position> - kill some NPCs in region");
                    m_log.Info ("[NPCModule]:   npc ls [<part of name or position>] - list all or some NPCs in region");
                    break;
                }
                case "kill": {
                    Dictionary<UUID,string> knownNames = new Dictionary<UUID,string> ();
                    string names = GetNPCNames (args, 2);
                    if (names == "") {
                        m_log.Info ("[NPCModule]: nothing given to kill, use 'npc kill <part of name or position>'");
                        break;
                    }
                    List<NPCAvatar> tokill = new List<NPCAvatar> ();
                    lock (m_avatars) {
                        foreach (NPCAvatar npcav in m_avatars.Values) {
                            string avls = npcav.Name + " " + npcav.Position + " " + GetAvName (npcav.OwnerID, npcav.Scene, knownNames);
                            if (avls.Contains (names)) {
                                tokill.Add (npcav);
                            }
                        }
                    }
                    foreach (NPCAvatar npcav in tokill) {
                        DeleteNPC (npcav.AgentId, npcav.Scene);
                    }
                    break;
                }
                case "ls": {
                    Dictionary<UUID,string> knownNames = new Dictionary<UUID,string> ();
                    string names  = GetNPCNames (args, 2);
                    int w_npc = "NPC".Length;
                    int w_pos = "Position".Length;
                    lock (m_avatars) {
                        foreach (NPCAvatar npcav in m_avatars.Values) {
                            int l_npc = npcav.Name.Length;
                            int l_pos = npcav.Position.ToString ().Length;
                            if (w_npc < l_npc) w_npc = l_npc;
                            if (w_pos < l_pos) w_pos = l_pos;
                        }
                        w_npc += 2;
                        w_pos += 2;
                        string avls = "NPC".PadRight (w_npc) + "Position".PadRight (w_pos) + "Owner";
                        m_log.Info ("[NPCModule]:  " + avls);
                        foreach (NPCAvatar npcav in m_avatars.Values) {
                            avls = npcav.Name.PadRight (w_npc) +
                                   npcav.Position.ToString ().PadRight (w_pos) +
                                   GetAvName (npcav.OwnerID, npcav.Scene, knownNames);
                            if (avls.Contains (names)) {
                                m_log.Info ("[NPCModule]:  " + avls);
                            }
                        }
                    }
                    break;
                }
                default: {
                    m_log.Info ("[NPCModule]: unknown command " + args[1] + ", try 'npc help'");
                    break;
                }
            }
        }

        private static string GetNPCNames (string[] args, int i)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder ();
            for (; i < args.Length; i ++) {
                if (i > 2) sb.Append (' ');
                sb.Append (args[i]);
            }
            return sb.ToString ();
        }

        private static string GetAvName (UUID avuuid, IScene iscene, Dictionary<UUID,string> knownNames)
        {
            string avname;
            if (!knownNames.TryGetValue (avuuid, out avname)) {
                avname = avuuid.ToString ();
                if (iscene is Scene) {
                    Scene scene = (Scene)iscene;
                    UserAccount account = scene.UserAccountService.GetUserAccount (scene.RegionInfo.ScopeID, avuuid);
                    if (account != null) {
                        avname = account.FirstName + " " + account.LastName;
                    }
                }
                knownNames.Add (avuuid, avname);
            }
            return avname;
        }
    }
}
