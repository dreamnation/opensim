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
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Client;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.Avatar.InstantMessage
{
    public struct SendReply
    {
        public bool Success;
        public string Message;
        public int Disposition;
    }

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "OfflineMessageModule")]
    public class OfflineMessageModule : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool enabled = true;
        private bool m_UseNewAvnCode = false;
        private List<Scene> m_SceneList = new List<Scene>();
        private string m_RestURL = String.Empty;
        IMessageTransferModule m_TransferModule = null;
        ITranslatorModule m_TranslatorModule;
        private bool m_ForwardOfflineGroupMessages = true;
        private Dictionary<IClientAPI, List<UUID>> m_repliesSent= new Dictionary<IClientAPI, List<UUID>>();

        public void Initialise(IConfigSource config)
        {
            IConfig cnf = config.Configs["Messaging"];
            if (cnf == null)
            {
                enabled = false;
                return;
            }
            if (cnf != null && cnf.GetString("OfflineMessageModule", "None") !=
                    "OfflineMessageModule")
            {
                enabled = false;
                return;
            }

            m_RestURL = cnf.GetString("OfflineMessageURL", "");
            if (m_RestURL == "")
            {
                m_log.Error("[OFFLINE MESSAGING] Module was enabled, but no URL is given, disabling");
                enabled = false;
                return;
            }

            m_ForwardOfflineGroupMessages = cnf.GetBoolean("ForwardOfflineGroupMessages", m_ForwardOfflineGroupMessages);
            m_UseNewAvnCode = cnf.GetBoolean("UseNewAvnCode", m_UseNewAvnCode);
        }

        public void AddRegion(Scene scene)
        {
            if (!enabled)
                return;

            lock (m_SceneList)
            {
                m_SceneList.Add(scene);

                scene.EventManager.OnNewClient += OnNewClient;
            }
        }

        public void RegionLoaded(Scene scene)
        {
            if (!enabled)
                return;

            if (m_TransferModule == null)
            {
                m_TransferModule = scene.RequestModuleInterface<IMessageTransferModule>();
                if (m_TransferModule == null)
                {
                    scene.EventManager.OnNewClient -= OnNewClient;

                    enabled = false;
                    m_SceneList.Clear();

                    m_log.Error("[OFFLINE MESSAGING] No message transfer module is enabled. Diabling offline messages");
                }
                m_TransferModule.OnUndeliveredMessage += UndeliveredMessage;
            }

            m_TranslatorModule = scene.RequestModuleInterface<ITranslatorModule> ();
        }

        public void RemoveRegion(Scene scene)
        {
            if (!enabled)
                return;

            lock (m_SceneList)
            {
                m_SceneList.Remove(scene);
            }
        }

        public void PostInitialise()
        {
            if (!enabled)
                return;

            m_log.Debug("[OFFLINE MESSAGING] Offline messages enabled");
        }

        public string Name
        {
            get { return "OfflineMessageModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Close()
        {
        }

        private Scene FindScene(UUID agentID)
        {
            foreach (Scene s in m_SceneList)
            {
                ScenePresence presence = s.GetScenePresence(agentID);
                if (presence != null && !presence.IsChildAgent)
                    return s;
            }
            return null;
        }

        private IClientAPI FindClient(UUID agentID)
        {
            foreach (Scene s in m_SceneList)
            {
                ScenePresence presence = s.GetScenePresence(agentID);
                if (presence != null && !presence.IsChildAgent)
                    return presence.ControllingClient;
            }
            return null;
        }

        private void OnNewClient(IClientAPI client)
        {
            client.OnRetrieveInstantMessages += RetrieveInstantMessages;
            client.OnLogout += OnClientLoggedOut;
        }

        public void OnClientLoggedOut(IClientAPI client)
        {
            m_repliesSent.Remove(client);
        }

        private void RetrieveInstantMessages(IClientAPI client)
        {
            if (m_RestURL == String.Empty)
            {
                return;
            }
            else
            {
                m_log.DebugFormat("[OFFLINE MESSAGING]: Retrieving stored messages for {0}", client.AgentId);

                List<GridInstantMessage> msglist
                    = SynchronousRestObjectRequester.MakeRequest<UUID, List<GridInstantMessage>>(
                        "POST", m_RestURL + "/RetrieveMessages/", client.AgentId);

                if (msglist != null)
                {
                    foreach (GridInstantMessage im in msglist)
                    {
                        // If translator active, the message is already translated to this avatar's language
                        // so don't try to translate it again.  tagging it with [[[--]]] will cause the
                        // translator to just pass it through to the avatar without translation.
                        if ((m_TranslatorModule != null) && !im.message.StartsWith ("[[[")) {
                            im.message = "[[[--]]]" + im.message;
                        }

                        if (im.dialog == (byte)InstantMessageDialog.InventoryOffered)
                            // send it directly or else the item will be given twice
                            client.SendInstantMessage(im);
                        else
                        {
                            // Send through scene event manager so all modules get a chance
                            // to look at this message before it gets delivered.
                            //
                            // Needed for proper state management for stored group
                            // invitations
                            //

                            im.offline = 1;

                            Scene s = FindScene(client.AgentId);
                            if (s != null)
                                s.EventManager.TriggerIncomingInstantMessage(im);
                        }
                    }
                }
            }
        }

        private void UndeliveredMessage(GridInstantMessage im)
        {
            if (im.dialog != (byte)InstantMessageDialog.MessageFromObject &&
                im.dialog != (byte)InstantMessageDialog.MessageFromAgent &&
                im.dialog != (byte)InstantMessageDialog.GroupNotice &&
                im.dialog != (byte)InstantMessageDialog.GroupInvitation &&
                im.dialog != (byte)InstantMessageDialog.InventoryOffered &&
                im.dialog != (byte)InstantMessageDialog.TaskInventoryOffered)
            {
                return;
            }

            if (!m_ForwardOfflineGroupMessages)
            {
                if (im.dialog == (byte)InstantMessageDialog.GroupNotice ||
                    im.dialog == (byte)InstantMessageDialog.GroupInvitation)
                    return;
            }

            SaveMessage sm = new SaveMessage ();
            sm.im  = im;
            sm.omm = this;

            if (m_TranslatorModule == null) {
                sm.Finished (im.message);
            } else {
                int i = im.message.IndexOf ('|');
                if (i < 0) {

                    /*
                     * Just a message body, translate in one piece.
                     */
                    m_TranslatorModule.WhatevToAgent (im.toAgentID.ToString (), sm.Finished, im.message);
                } else {

                    /*
                     * Title and body, translate each separately in case of showoriginal mode.
                     */
                    string title = im.message.Substring (0, i);
                    string body  = im.message.Substring (++ i);
                    if (title.StartsWith ("[[[") && ((i = title.IndexOf ("]]]")) >= 0)) {
                        body = title.Substring (0, i + 3) + body;
                    }
                    m_log.Debug ("[OfflineMessageModule]: UndeliveredMessage*: title=" + title);
                    m_log.Debug ("[OfflineMessageModule]: UndeliveredMessage*: body=" + body);
                    m_TranslatorModule.WhatevToAgent (im.toAgentID.ToString (), sm.FinTitle, title);
                    m_TranslatorModule.WhatevToAgent (im.toAgentID.ToString (), sm.FinBody, body);
                }
            }
        }

        private class SaveMessage {
            public GridInstantMessage im;
            public OfflineMessageModule omm;

            private int both;
            private string xtitle;
            private string xbody;

            /**
             * @brief Finished translating title of split title|body message.
             */
            public void FinTitle (string message)
            {
                lock (this) {
                    m_log.Debug ("[OfflineMessageModule]: FinTitle*: message=" + message);
                    xtitle = message.Replace ('\n', ' ');
                    BothPartsFinished ();
                }
            }

            /**
             * @brief Finished translating body of split title|body message.
             */
            public void FinBody (string message)
            {
                lock (this) {
                    m_log.Debug ("[OfflineMessageModule]: FinBody*: message=" + message);
                    xbody = message;
                    BothPartsFinished ();
                }
            }

            /**
             * @brief If both parts of split message are translated,
             *        post the composite translation.
             */
            private void BothPartsFinished ()
            {
                if (++ both == 2) {
                    if ((xtitle != null) && (xbody != null)) {
                        Finished (xtitle + "|" + xbody);
                    } else {
                        Finished (null);
                    }
                }
            }

            /**
             * @brief Both parts of split message translated,
             *        or body-only message translated.
             */
            public void Finished (string message)
            {
                bool success = false;

                if (message != null) {
                    im.message = message;
                    success = SynchronousRestObjectRequester.MakeRequest<GridInstantMessage, bool>(
                            "POST", omm.m_RestURL+"/SaveMessage/", im, 20000);
                }

                if (im.dialog == (byte)InstantMessageDialog.MessageFromAgent) {
                    IClientAPI client = omm.FindClient(new UUID(im.fromAgentID));
                    if (client != null) {
                        client.SendInstantMessage(new GridInstantMessage(
                                null, new UUID(im.toAgentID),
                                "System", new UUID(im.fromAgentID),
                                (byte)InstantMessageDialog.MessageFromAgent,
                                "User is not logged in. "+
                                (success ? "Message saved." : "Message not saved."),
                                false, new Vector3()));
                    }
                }
            }
        }
    }
}
