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
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.IO;
using OpenMetaverse.StructuredData;
using OpenMetaverse;
using log4net;

namespace OpenSim.Framework.Servers.HttpServer
{
    /// <summary>
    /// Json rpc request manager.
    /// </summary>
    public class JsonRpcRequestManager
    {
        static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public JsonRpcRequestManager()
        {
        }

        /// <summary>
        /// Sends json-rpc request with a serializable type.
        /// </summary>
        /// <returns>
        /// OSD Map.
        /// </returns>
        /// <param name='parameters'>
        /// Serializable type .
        /// </param>
        /// <param name='method'>
        /// Json-rpc method to call.
        /// </param>
        /// <param name='uri'>
        /// URI of json-rpc service.
        /// </param>
        /// <param name='jsonId'>
        /// Id for our call.
        /// </param>
        public bool JsonRpcRequest(ref object parameters, string method, string uri, string jsonId)
        {
            if (jsonId == null)
                throw new ArgumentNullException("jsonId");
            if (uri == null)
                throw new ArgumentNullException("uri");
            if (method == null)
                throw new ArgumentNullException("method");
            if (parameters == null)
                throw new ArgumentNullException("parameters");

            m_log.Debug ("[JsonRpcRequestManager] JsonRpcRequest*: method=" + method + " uri=" + uri);

            if(string.IsNullOrWhiteSpace(uri))
                return false;

            OSDMap request = new OSDMap();
            request.Add("jsonrpc", OSD.FromString("2.0"));
            request.Add("id", OSD.FromString(jsonId));
            request.Add("method", OSD.FromString(method));
            request.Add("params", OSD.SerializeMembers(parameters));

            OSDMap response;
            try
            {
                response = WebUtil.PostToService(uri, request, 10000, true);
            }
            catch (Exception e)
            {
                m_log.Debug(string.Format("JsonRpc request '{0}' to {1} failed", method, uri), e);
                return false;
            }

            // parse JSON separately from PostToService cuz libopenmetaverse OSDJson.cs messes up bigly
            if (response.ContainsKey ("_RawResult")) {

                // get raw string returned by webserver and
                // see if it starts with '{' and ends with '}' indicating JSON
                string raw = response["_RawResult"].ToString ().Trim ();
                if (raw.StartsWith ("{") && raw.EndsWith ("}")) {
                    m_log.Debug ("[JsonRpcRequestManager] JsonRpcRequest*: raw=<" + raw + ">");

                    // all we need are the "key":primvalue phrases
                    Dictionary<string,object> kvps = new Dictionary<string,object> ();
                    int rawlen = raw.Length;
                    string key = null;
                    object val = null;
                    for (int i = 0; i < rawlen; i ++) {
                        char c = raw[i];
                        switch (c) {

                            // discard any key before these as the value isn't a primitive
                            case '{':
                            case '[': {
                                key = null;
                                val = null;
                                break;
                            }

                            // quoted string key or primitive value
                            case '"': {
                                StringBuilder sb = new StringBuilder (rawlen - i);
                                while (++ i < rawlen) {
                                    c = raw[i];
                                    if (c == '"') break;
                                    if ((c == '\\') && (++ i < rawlen)) {
                                        c = raw[i];
                                        switch (c) {
                                            case 'b': c = '\b'; break;
                                            case 'n': c = '\n'; break;
                                            case 'r': c = '\r'; break;
                                            case 't': c = '\t'; break;
                                        }
                                    }
                                    sb.Append (c);
                                }
                                val = sb.ToString ();
                                break;
                            }

                            // value just before colon is a key as a string
                            case ':': {
                                key = (string) val;
                                val = null;
                                break;
                            }

                            // other primitive values: null, boolean, integer, double
                            default: {
                                if (c <= ' ') break;

                                // find end of value, first of } ] , space
                                int j = i;
                                while (++ j < rawlen) {
                                    c = raw[j];
                                    if (c <= ' ') break;
                                    if ((c == '}') || (c == ']') || (c == ',')) break;
                                }

                                // get value as a lower case string
                                string rlc = raw.Substring (i, j - i).ToLowerInvariant ();

                                // next through loop proceses the } ] , space
                                i = -- j;

                                // must be one of these
                                double d;
                                int k;
                                     if (rlc == "null")  val = null;
                                else if (rlc == "false") val = false;
                                else if (rlc == "true")  val = true;
                                else if (int.TryParse (rlc, out k)) val = k;
                                else if (double.TryParse (rlc, out d)) val = d;
                                else throw new ApplicationException ("bad json value " + rlc);
                                break;
                            }

                            // these come after a possible primitive value
                            case '}':
                            case ']':
                            case ',': {
                                if (key != null) {
                                    kvps[key.ToLowerInvariant()] = val;
                                }
                                key = null;
                                val = null;
                                break;
                            }
                        }
                    }
                    foreach (string kkk in kvps.Keys) {
                        val = kvps[kkk];
                        m_log.Debug ("[JsonRpcRequestManager] JsonRpcRequest*: kvps[" + kkk + "]=(" + val.GetType ().Name + ")" + val.ToString ());
                    }

                    // scan through all serializable fields of the given object
                    Type t = parameters.GetType();
                    FieldInfo[] fields = t.GetFields();
                    for (int i = 0; i < fields.Length; i++) {
                        FieldInfo field = fields[i];
                        if (!Attribute.IsDefined (field, typeof (NonSerializedAttribute))) {

                            // see if there is a like-named field present in the JSON string
                            key = field.Name.ToLowerInvariant ();
                            if (kvps.TryGetValue (key, out val)) {
                                m_log.Debug ("[JsonRpcRequestManager] JsonRpcRequest*: " + field.FieldType.Name + " " + t.Name + "." + field.Name + "=" + val.ToString ());

                                // store value in given object
                                field.SetValue (parameters, val);
                            }
                        }
                    }
                    return true;
                }
            }

            if (!response.ContainsKey("_Result"))
            {
                m_log.DebugFormat("JsonRpc request '{0}' to {1} returned an invalid response: {2}",
                    method, uri, OSDParser.SerializeJsonString(response));
                return false;
            }
            response = (OSDMap)response["_Result"];

            OSD data;

            if (response.ContainsKey("error"))
            {
                data = response["error"];
                m_log.DebugFormat("JsonRpc request '{0}' to {1} returned an error: {2}",
                    method, uri, OSDParser.SerializeJsonString(data));
                return false;
            }

            if (!response.ContainsKey("result"))
            {
                m_log.DebugFormat("JsonRpc request '{0}' to {1} returned an invalid response: {2}",
                    method, uri, OSDParser.SerializeJsonString(response));
                return false;
            }

            data = response["result"];
            OSD.DeserializeMembers(ref parameters, (OSDMap)data);

            return true;
        }

        /// <summary>
        /// Sends json-rpc request with OSD parameter.
        /// </summary>
        /// <returns>
        /// The rpc request.
        /// </returns>
        /// <param name='data'>
        /// data - incoming as parameters, outgoing as result/error
        /// </param>
        /// <param name='method'>
        /// Json-rpc method to call.
        /// </param>
        /// <param name='uri'>
        /// URI of json-rpc service.
        /// </param>
        /// <param name='jsonId'>
        /// If set to <c>true</c> json identifier.
        /// </param>
        public bool JsonRpcRequest(ref OSD data, string method, string uri, string jsonId)
        {
            if (string.IsNullOrEmpty(jsonId))
                jsonId = UUID.Random().ToString();

            OSDMap request = new OSDMap();
            request.Add("jsonrpc", OSD.FromString("2.0"));
            request.Add("id", OSD.FromString(jsonId));
            request.Add("method", OSD.FromString(method));
            request.Add("params", data);

            OSDMap response;
            try
            {
                response = WebUtil.PostToService(uri, request, 10000, true);
            }
            catch (Exception e)
            {
                m_log.Debug(string.Format("JsonRpc request '{0}' to {1} failed", method, uri), e);
                return false;
            }

            if (!response.ContainsKey("_Result"))
            {
                m_log.DebugFormat("JsonRpc request '{0}' to {1} returned an invalid response: {2}",
                    method, uri, OSDParser.SerializeJsonString(response));
                return false;
            }
            response = (OSDMap)response["_Result"];

            if (response.ContainsKey("error"))
            {
                data = response["error"];
                m_log.DebugFormat("JsonRpc request '{0}' to {1} returned an error: {2}",
                    method, uri, OSDParser.SerializeJsonString(data));
                return false;
            }

            data = response;

            return true;
        }

    }
}
