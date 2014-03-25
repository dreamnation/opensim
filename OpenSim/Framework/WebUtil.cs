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
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Text;
using System.Web;
using System.Xml;
using System.Xml.Serialization;
using log4net;
using OpenMetaverse.StructuredData;

namespace OpenSim.Framework
{
    /// <summary>
    /// Miscellaneous static methods and extension methods related to the web
    /// </summary>
    public static class WebUtil
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Control the printing of certain debug messages.
        /// </summary>
        /// <remarks>
        /// If DebugLevel >= 3 then short notices about outgoing HTTP requests are logged.
        /// </remarks>
        public static int DebugLevel { get; set; }

        /// <summary>
        /// Request number for diagnostic purposes.
        /// </summary>
        public static int RequestNumber { get; internal set; }

        /// <summary>
        /// Control where OSD requests should be serialized per endpoint.
        /// </summary>
        public static bool SerializeOSDRequestsPerEndpoint { get; set; }

        /// <summary>
        /// this is the header field used to communicate the local request id
        /// used for performance and debugging
        /// </summary>
        public const string OSHeaderRequestID = "opensim-request-id";

        /// <summary>
        /// Number of milliseconds a call can take before it is considered
        /// a "long" call for warning & debugging purposes
        /// </summary>
        public const int LongCallTime = 3000;

        /// <summary>
        /// The maximum length of any data logged because of a long request time.
        /// </summary>
        /// <remarks>
        /// This is to truncate any really large post data, such as an asset.  In theory, the first section should
        /// give us useful information about the call (which agent it relates to if applicable, etc.).
        /// </remarks>
        public const int MaxRequestDiagLength = 100;

        /// <summary>
        /// Dictionary of end points
        /// </summary>
        private static Dictionary<string,object> m_endpointSerializer = new Dictionary<string,object>();

        private static object EndPointLock(string url)
        {
            System.Uri uri = new System.Uri(url);
            string endpoint = string.Format("{0}:{1}",uri.Host,uri.Port);

            lock (m_endpointSerializer)
            {
                object eplock = null;

                if (! m_endpointSerializer.TryGetValue(endpoint,out eplock))
                {
                    eplock = new object();
                    m_endpointSerializer.Add(endpoint,eplock);
                    // m_log.WarnFormat("[WEB UTIL] add a new host to end point serializer {0}",endpoint);
                }

                return eplock;
            }
        }

        #region JSONRequest

        /// <summary>
        /// PUT JSON-encoded data to a web service that returns LLSD or
        /// JSON data
        /// </summary>
        public static OSDMap PutToServiceCompressed(string url, OSDMap data, int timeout)
        {
            return ServiceOSDRequest(url,data, "PUT", timeout, true);
        }

        public static OSDMap PutToService(string url, OSDMap data, int timeout)
        {
            return ServiceOSDRequest(url,data, "PUT", timeout, false);
        }

        public static OSDMap PostToService(string url, OSDMap data, int timeout)
        {
            return ServiceOSDRequest(url, data, "POST", timeout, false);
        }

        public static OSDMap PostToServiceCompressed(string url, OSDMap data, int timeout)
        {
            return ServiceOSDRequest(url, data, "POST", timeout, true);
        }

        public static OSDMap GetFromService(string url, int timeout)
        {
            return ServiceOSDRequest(url, null, "GET", timeout, false);
        }
        
        public static OSDMap ServiceOSDRequest(string url, OSDMap data, string method, int timeout, bool compressed)
        {
            if (SerializeOSDRequestsPerEndpoint)
            {
                lock (EndPointLock(url))
                {
                    return ServiceOSDRequestWorker(url, data, method, timeout, compressed);
                }
            }
            else
            {
                return ServiceOSDRequestWorker(url, data, method, timeout, compressed);
            }
        }

        public static void LogOutgoingDetail(Stream outputStream)
        {
            using (StreamReader reader = new StreamReader(Util.Copy(outputStream), Encoding.UTF8))
            {
                string output;

                if (DebugLevel == 5)
                {
                    const int sampleLength = 80;
                    char[] sampleChars = new char[sampleLength];
                    reader.Read(sampleChars, 0, sampleLength);
                    output = new string(sampleChars);
                }
                else
                {
                    output = reader.ReadToEnd();
                }

                LogOutgoingDetail(output);
            }
        }

        public static void LogOutgoingDetail(string output)
        {
            if (DebugLevel == 5)
            {
                output = output.Substring(0, 80);
                output = output + "...";
            }

            m_log.DebugFormat("[WEB UTIL]: {0}", output.Replace("\n", @"\n"));
        }

        private static OSDMap ServiceOSDRequestWorker(string url, OSDMap data, string method, int timeout, bool compressed)
        {
            int reqnum = RequestNumber++;

            if (DebugLevel >= 3)
                m_log.DebugFormat(
                    "[WEB UTIL]: HTTP OUT {0} ServiceOSD {1} {2} (timeout {3}, compressed {4})",
                    reqnum, method, url, timeout, compressed);

            string errorMessage = "unknown error";
            int tickstart = Util.EnvironmentTickCount();
            int tickdata = 0;
            string strBuffer = null;

            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = method;
                request.Timeout = timeout;
                request.KeepAlive = false;
                request.MaximumAutomaticRedirections = 10;
                request.ReadWriteTimeout = timeout / 4;
                request.Headers[OSHeaderRequestID] = reqnum.ToString();
                
                // If there is some input, write it into the request
                if (data != null)
                {
                    strBuffer = OSDParser.SerializeJsonString(data);

                    if (DebugLevel >= 5)
                        LogOutgoingDetail(strBuffer);

                    byte[] buffer = System.Text.Encoding.UTF8.GetBytes(strBuffer);

                    if (compressed)
                    {
                        request.ContentType = "application/x-gzip";
                        using (MemoryStream ms = new MemoryStream())
                        {
                            using (GZipStream comp = new GZipStream(ms, CompressionMode.Compress))
                            {
                                comp.Write(buffer, 0, buffer.Length);
                                // We need to close the gzip stream before we write it anywhere
                                // because apparently something important related to gzip compression
                                // gets written on the strteam upon Dispose()
                            }
                            byte[] buf = ms.ToArray();
                            request.ContentLength = buf.Length;   //Count bytes to send
                            using (Stream requestStream = request.GetRequestStream())
                                requestStream.Write(buf, 0, (int)buf.Length);
                        }
                    }
                    else
                    {
                        request.ContentType = "application/json";
                        request.ContentLength = buffer.Length;   //Count bytes to send
                        using (Stream requestStream = request.GetRequestStream())
                                requestStream.Write(buffer, 0, buffer.Length);         //Send it
                    }
                }
                
                // capture how much time was spent writing, this may seem silly
                // but with the number concurrent requests, this often blocks
                tickdata = Util.EnvironmentTickCountSubtract(tickstart);

                using (WebResponse response = request.GetResponse())
                {
                    using (Stream responseStream = response.GetResponseStream())
                    {
                        string responseStr = null;
                        responseStr = responseStream.GetStreamString();
                        // m_log.DebugFormat("[WEB UTIL]: <{0}> response is <{1}>",reqnum,responseStr);
                        return CanonicalizeResults(responseStr);
                    }
                }
            }
            catch (WebException we)
            {
                errorMessage = we.Message;
                if (we.Status == WebExceptionStatus.ProtocolError)
                {
                    using (HttpWebResponse webResponse = (HttpWebResponse)we.Response)
                        errorMessage = String.Format("[{0}] {1}", webResponse.StatusCode, webResponse.StatusDescription);
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
            }
            finally
            {
                int tickdiff = Util.EnvironmentTickCountSubtract(tickstart);
                if (tickdiff > LongCallTime)
                    m_log.InfoFormat(
                        "[WEB UTIL]: Slow ServiceOSD request {0} {1} {2} took {3}ms, {4}ms writing, {5}",
                        reqnum,
                        method,
                        url,
                        tickdiff,
                        tickdata,
                        strBuffer != null
                            ? (strBuffer.Length > MaxRequestDiagLength ? strBuffer.Remove(MaxRequestDiagLength) : strBuffer)
                            : "");
                else if (DebugLevel >= 4)
                    m_log.DebugFormat(
                        "[WEB UTIL]: HTTP OUT {0} took {1}ms, {2}ms writing",
                        reqnum, tickdiff, tickdata);
            }
           
            m_log.DebugFormat(
                "[WEB UTIL]: ServiceOSD request {0} {1} {2} FAILED: {3}", reqnum, url, method, errorMessage);

            return ErrorResponseMap(errorMessage);
        }

        /// <summary>
        /// Since there are no consistencies in the way web requests are
        /// formed, we need to do a little guessing about the result format.
        /// Keys:
        ///     Success|success == the success fail of the request
        ///     _RawResult == the raw string that came back
        ///     _Result == the OSD unpacked string
        /// </summary>
        private static OSDMap CanonicalizeResults(string response)
        {
            OSDMap result = new OSDMap();

            // Default values
            result["Success"] = OSD.FromBoolean(true);
            result["success"] = OSD.FromBoolean(true);
            result["_RawResult"] = OSD.FromString(response);
            result["_Result"] = new OSDMap();
            
            if (response.Equals("true",System.StringComparison.OrdinalIgnoreCase))
                return result;

            if (response.Equals("false",System.StringComparison.OrdinalIgnoreCase))
            {
                result["Success"] = OSD.FromBoolean(false);
                result["success"] = OSD.FromBoolean(false);
                return result;
            }

            try 
            {
                OSD responseOSD = OSDParser.Deserialize(response);
                if (responseOSD.Type == OSDType.Map)
                {
                    result["_Result"] = (OSDMap)responseOSD;
                    return result;
                }
            }
            catch
            {
                // don't need to treat this as an error... we're just guessing anyway
//                m_log.DebugFormat("[WEB UTIL] couldn't decode <{0}>: {1}",response,e.Message);
            }
            
            return result;
        }
        
        #endregion JSONRequest

        #region FormRequest

        /// <summary>
        /// POST URL-encoded form data to a web service that returns LLSD or
        /// JSON data
        /// </summary>
        public static OSDMap PostToService(string url, NameValueCollection data)
        {
            return ServiceFormRequest(url,data,10000);
        }
        
        public static OSDMap ServiceFormRequest(string url, NameValueCollection data, int timeout)
        {
            lock (EndPointLock(url))
            {
                return ServiceFormRequestWorker(url,data,timeout);
            }
        }

        private static OSDMap ServiceFormRequestWorker(string url, NameValueCollection data, int timeout)
        {
            int reqnum = RequestNumber++;
            string method = (data != null && data["RequestMethod"] != null) ? data["RequestMethod"] : "unknown";

            if (DebugLevel >= 3)
                m_log.DebugFormat(
                    "[WEB UTIL]: HTTP OUT {0} ServiceForm {1} {2} (timeout {3})",
                    reqnum, method, url, timeout);
            
            string errorMessage = "unknown error";
            int tickstart = Util.EnvironmentTickCount();
            int tickdata = 0;
            string queryString = null;

            try
            {
                HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(url);
                request.Method = "POST";
                request.Timeout = timeout;
                request.KeepAlive = false;
                request.MaximumAutomaticRedirections = 10;
                request.ReadWriteTimeout = timeout / 4;
                request.Headers[OSHeaderRequestID] = reqnum.ToString();
                
                if (data != null)
                {
                    queryString = BuildQueryString(data);

                    if (DebugLevel >= 5)
                        LogOutgoingDetail(queryString);

                    byte[] buffer = System.Text.Encoding.UTF8.GetBytes(queryString);
                    
                    request.ContentLength = buffer.Length;
                    request.ContentType = "application/x-www-form-urlencoded";
                    using (Stream requestStream = request.GetRequestStream())
                        requestStream.Write(buffer, 0, buffer.Length);
                }

                // capture how much time was spent writing, this may seem silly
                // but with the number concurrent requests, this often blocks
                tickdata = Util.EnvironmentTickCountSubtract(tickstart);

                using (WebResponse response = request.GetResponse())
                {
                    using (Stream responseStream = response.GetResponseStream())
                    {
                        string responseStr = null;

                        responseStr = responseStream.GetStreamString();
                        OSD responseOSD = OSDParser.Deserialize(responseStr);
                        if (responseOSD.Type == OSDType.Map)
                            return (OSDMap)responseOSD;
                    }
                }
            }
            catch (WebException we)
            {
                errorMessage = we.Message;
                if (we.Status == WebExceptionStatus.ProtocolError)
                {
                    using (HttpWebResponse webResponse = (HttpWebResponse)we.Response)
                        errorMessage = String.Format("[{0}] {1}",webResponse.StatusCode,webResponse.StatusDescription);
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
            }
            finally
            {
                int tickdiff = Util.EnvironmentTickCountSubtract(tickstart);
                if (tickdiff > LongCallTime)
                    m_log.InfoFormat(
                        "[WEB UTIL]: Slow ServiceForm request {0} {1} {2} took {3}ms, {4}ms writing, {5}",
                        reqnum,
                        method,
                        url,
                        tickdiff,
                        tickdata,
                        queryString != null
                            ? (queryString.Length > MaxRequestDiagLength) ? queryString.Remove(MaxRequestDiagLength) : queryString
                            : "");
                else if (DebugLevel >= 4)
                    m_log.DebugFormat(
                        "[WEB UTIL]: HTTP OUT {0} took {1}ms, {2}ms writing",
                        reqnum, tickdiff, tickdata);
            }

            m_log.WarnFormat("[WEB UTIL]: ServiceForm request {0} {1} {2} failed: {2}", reqnum, method, url, errorMessage);

            return ErrorResponseMap(errorMessage);
        }

        /// <summary>
        /// Create a response map for an error, trying to keep
        /// the result formats consistent
        /// </summary>
        private static OSDMap ErrorResponseMap(string msg)
        {
            OSDMap result = new OSDMap();
            result["Success"] = "False";
            result["Message"] = OSD.FromString("Service request failed: " + msg);
            return result;
        }

        #endregion FormRequest
        
        #region Uri

        /// <summary>
        /// Combines a Uri that can contain both a base Uri and relative path
        /// with a second relative path fragment
        /// </summary>
        /// <param name="uri">Starting (base) Uri</param>
        /// <param name="fragment">Relative path fragment to append to the end
        /// of the Uri</param>
        /// <returns>The combined Uri</returns>
        /// <remarks>This is similar to the Uri constructor that takes a base
        /// Uri and the relative path, except this method can append a relative
        /// path fragment on to an existing relative path</remarks>
        public static Uri Combine(this Uri uri, string fragment)
        {
            string fragment1 = uri.Fragment;
            string fragment2 = fragment;

            if (!fragment1.EndsWith("/"))
                fragment1 = fragment1 + '/';
            if (fragment2.StartsWith("/"))
                fragment2 = fragment2.Substring(1);

            return new Uri(uri, fragment1 + fragment2);
        }

        /// <summary>
        /// Combines a Uri that can contain both a base Uri and relative path
        /// with a second relative path fragment. If the fragment is absolute,
        /// it will be returned without modification
        /// </summary>
        /// <param name="uri">Starting (base) Uri</param>
        /// <param name="fragment">Relative path fragment to append to the end
        /// of the Uri, or an absolute Uri to return unmodified</param>
        /// <returns>The combined Uri</returns>
        public static Uri Combine(this Uri uri, Uri fragment)
        {
            if (fragment.IsAbsoluteUri)
                return fragment;

            string fragment1 = uri.Fragment;
            string fragment2 = fragment.ToString();

            if (!fragment1.EndsWith("/"))
                fragment1 = fragment1 + '/';
            if (fragment2.StartsWith("/"))
                fragment2 = fragment2.Substring(1);

            return new Uri(uri, fragment1 + fragment2);
        }

        /// <summary>
        /// Appends a query string to a Uri that may or may not have existing 
        /// query parameters
        /// </summary>
        /// <param name="uri">Uri to append the query to</param>
        /// <param name="query">Query string to append. Can either start with ?
        /// or just containg key/value pairs</param>
        /// <returns>String representation of the Uri with the query string
        /// appended</returns>
        public static string AppendQuery(this Uri uri, string query)
        {
            if (String.IsNullOrEmpty(query))
                return uri.ToString();

            if (query[0] == '?' || query[0] == '&')
                query = query.Substring(1);

            string uriStr = uri.ToString();

            if (uriStr.Contains("?"))
                return uriStr + '&' + query;
            else
                return uriStr + '?' + query;
        }

        #endregion Uri

        #region NameValueCollection

        /// <summary>
        /// Convert a NameValueCollection into a query string. This is the
        /// inverse of HttpUtility.ParseQueryString()
        /// </summary>
        /// <param name="parameters">Collection of key/value pairs to convert</param>
        /// <returns>A query string with URL-escaped values</returns>
        public static string BuildQueryString(NameValueCollection parameters)
        {
            List<string> items = new List<string>(parameters.Count);

            foreach (string key in parameters.Keys)
            {
                string[] values = parameters.GetValues(key);
                if (values != null)
                {
                    foreach (string value in values)
                        items.Add(String.Concat(key, "=", HttpUtility.UrlEncode(value ?? String.Empty)));
                }
            }

            return String.Join("&", items.ToArray());
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="collection"></param>
        /// <param name="key"></param>
        /// <returns></returns>
        public static string GetOne(this NameValueCollection collection, string key)
        {
            string[] values = collection.GetValues(key);
            if (values != null && values.Length > 0)
                return values[0];

            return null;
        }

        #endregion NameValueCollection

        #region Stream

        /// <summary>
        /// Copies the contents of one stream to another, starting at the 
        /// current position of each stream
        /// </summary>
        /// <param name="copyFrom">The stream to copy from, at the position 
        /// where copying should begin</param>
        /// <param name="copyTo">The stream to copy to, at the position where 
        /// bytes should be written</param>
        /// <param name="maximumBytesToCopy">The maximum bytes to copy</param>
        /// <returns>The total number of bytes copied</returns>
        /// <remarks>
        /// Copying begins at the streams' current positions. The positions are
        /// NOT reset after copying is complete.
        /// NOTE!! .NET 4.0 adds the method 'Stream.CopyTo(stream, bufferSize)'.
        /// This function could be replaced with that method once we move
        /// totally to .NET 4.0. For versions before, this routine exists.
        /// This routine used to be named 'CopyTo' but the int parameter has
        /// a different meaning so this method was renamed to avoid any confusion.
        /// </remarks>
        public static int CopyStream(this Stream copyFrom, Stream copyTo, int maximumBytesToCopy)
        {
            byte[] buffer = new byte[4096];
            int readBytes;
            int totalCopiedBytes = 0;

            while ((readBytes = copyFrom.Read(buffer, 0, Math.Min(4096, maximumBytesToCopy))) > 0)
            {
                int writeBytes = Math.Min(maximumBytesToCopy, readBytes);
                copyTo.Write(buffer, 0, writeBytes);
                totalCopiedBytes += writeBytes;
                maximumBytesToCopy -= writeBytes;
            }

            return totalCopiedBytes;
        }

        /// <summary>
        /// Converts an entire stream to a string, regardless of current stream
        /// position
        /// </summary>
        /// <param name="stream">The stream to convert to a string</param>
        /// <returns></returns>
        /// <remarks>When this method is done, the stream position will be 
        /// reset to its previous position before this method was called</remarks>
        public static string GetStreamString(this Stream stream)
        {
            string value = null;

            if (stream != null && stream.CanRead)
            {
                long rewindPos = -1;

                if (stream.CanSeek)
                {
                    rewindPos = stream.Position;
                    stream.Seek(0, SeekOrigin.Begin);
                }

                StreamReader reader = new StreamReader(stream);
                value = reader.ReadToEnd();

                if (rewindPos >= 0)
                    stream.Seek(rewindPos, SeekOrigin.Begin);
            }

            return value;
        }

        #endregion Stream

        public class QBasedComparer : IComparer
        {
            public int Compare(Object x, Object y)
            {
                float qx = GetQ(x);
                float qy = GetQ(y);
                return qy.CompareTo(qx); // descending order
            }

            private float GetQ(Object o)
            {
                // Example: image/png;q=0.9

                float qvalue = 1F;
                if (o is String)
                {
                    string mime = (string)o;
                    string[] parts = mime.Split(';');
                    if (parts.Length > 1)
                    {
                        string[] kvp = parts[1].Split('=');
                        if (kvp.Length == 2 && kvp[0] == "q")
                            float.TryParse(kvp[1], NumberStyles.Number, CultureInfo.InvariantCulture, out qvalue);
                    }
                }

                return qvalue;
            }
        }

        /// <summary>
        /// Takes the value of an Accept header and returns the preferred types
        /// ordered by q value (if it exists).
        /// Example input: image/jpg;q=0.7, image/png;q=0.8, image/jp2
        /// Exmaple output: ["jp2", "png", "jpg"]
        /// NOTE: This doesn't handle the semantics of *'s...
        /// </summary>
        /// <param name="accept"></param>
        /// <returns></returns>
        public static string[] GetPreferredImageTypes(string accept)
        {
            if (accept == null || accept == string.Empty)
                return new string[0];

            string[] types = accept.Split(new char[] { ',' });
            if (types.Length > 0)
            {
                List<string> list = new List<string>(types);
                list.RemoveAll(delegate(string s) { return !s.ToLower().StartsWith("image"); });
                ArrayList tlist = new ArrayList(list);
                tlist.Sort(new QBasedComparer());

                string[] result = new string[tlist.Count];
                for (int i = 0; i < tlist.Count; i++)
                {
                    string mime = (string)tlist[i];
                    string[] parts = mime.Split(new char[] { ';' });
                    string[] pair = parts[0].Split(new char[] { '/' });
                    if (pair.Length == 2)
                        result[i] = pair[1].ToLower();
                    else // oops, we don't know what this is...
                        result[i] = pair[0];
                }

                return result;
            }

            return new string[0];
        }
    }

    public static class AsynchronousRestObjectRequester
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Perform an asynchronous REST request.
        /// </summary>
        /// <param name="verb">GET or POST</param>
        /// <param name="requestUrl"></param>
        /// <param name="obj"></param>
        /// <param name="action"></param>
        /// <returns></returns>
        ///
        /// <exception cref="System.Net.WebException">Thrown if we encounter a
        /// network issue while posting the request.  You'll want to make
        /// sure you deal with this as they're not uncommon</exception>
        //
        public static void MakeRequest<TRequest, TResponse>(string verb,
                string requestUrl, TRequest obj, Action<TResponse> action)
        {
            MakeRequest<TRequest, TResponse>(verb, requestUrl, obj, action, 0);
        }

        public static void MakeRequest<TRequest, TResponse>(string verb,
                string requestUrl, TRequest obj, Action<TResponse> action,
                int maxConnections)
        {
            int reqnum = WebUtil.RequestNumber++;

            if (WebUtil.DebugLevel >= 3)
                m_log.DebugFormat(
                    "[WEB UTIL]: HTTP OUT {0} AsynchronousRequestObject {1} {2}",
                    reqnum, verb, requestUrl);

            int tickstart = Util.EnvironmentTickCount();
            int tickdata = 0;

            Type type = typeof(TRequest);

            WebRequest request = WebRequest.Create(requestUrl);
            HttpWebRequest ht = (HttpWebRequest)request;
            if (maxConnections > 0 && ht.ServicePoint.ConnectionLimit < maxConnections)
                ht.ServicePoint.ConnectionLimit = maxConnections;

            WebResponse response = null;
            TResponse deserial = default(TResponse);
            XmlSerializer deserializer = new XmlSerializer(typeof(TResponse));

            request.Method = verb;
            MemoryStream buffer = null;

            if (verb == "POST")
            {
                request.ContentType = "text/xml";

                buffer = new MemoryStream();

                XmlWriterSettings settings = new XmlWriterSettings();
                settings.Encoding = Encoding.UTF8;

                using (XmlWriter writer = XmlWriter.Create(buffer, settings))
                {
                    XmlSerializer serializer = new XmlSerializer(type);
                    serializer.Serialize(writer, obj);
                    writer.Flush();
                }

                int length = (int)buffer.Length;
                request.ContentLength = length;

                if (WebUtil.DebugLevel >= 5)
                    WebUtil.LogOutgoingDetail(buffer);

                request.BeginGetRequestStream(delegate(IAsyncResult res)
                {
                    Stream requestStream = request.EndGetRequestStream(res);

                    requestStream.Write(buffer.ToArray(), 0, length);
                    requestStream.Close();

                    // capture how much time was spent writing
                    tickdata = Util.EnvironmentTickCountSubtract(tickstart);

                    request.BeginGetResponse(delegate(IAsyncResult ar)
                    {
                        response = request.EndGetResponse(ar);
                        Stream respStream = null;
                        try
                        {
                            respStream = response.GetResponseStream();
                            deserial = (TResponse)deserializer.Deserialize(
                                    respStream);
                        }
                        catch (System.InvalidOperationException)
                        {
                        }
                        finally
                        {
                            // Let's not close this
                            //buffer.Close();
                            respStream.Close();
                            response.Close();
                        }

                        action(deserial);

                    }, null);
                }, null);
            }
            else
            {
                request.BeginGetResponse(delegate(IAsyncResult res2)
                {
                    try
                    {
                        // If the server returns a 404, this appears to trigger a System.Net.WebException even though that isn't
                        // documented in MSDN
                        response = request.EndGetResponse(res2);
    
                        Stream respStream = null;
                        try
                        {
                            respStream = response.GetResponseStream();
                            deserial = (TResponse)deserializer.Deserialize(respStream);
                        }
                        catch (System.InvalidOperationException)
                        {
                        }
                        finally
                        {
                            respStream.Close();
                            response.Close();
                        }
                    }
                    catch (WebException e)
                    {
                        if (e.Status == WebExceptionStatus.ProtocolError)
                        {
                            if (e.Response is HttpWebResponse)
                            {
                                using (HttpWebResponse httpResponse = (HttpWebResponse)e.Response)
                                {        
                                    if (httpResponse.StatusCode != HttpStatusCode.NotFound)
                                    {
                                        // We don't appear to be handling any other status codes, so log these feailures to that
                                        // people don't spend unnecessary hours hunting phantom bugs.
                                        m_log.DebugFormat(
                                            "[ASYNC REQUEST]: Request {0} {1} failed with unexpected status code {2}",
                                            verb, requestUrl, httpResponse.StatusCode);
                                    }
                                }
                            }
                        }
                        else
                        {
                            m_log.ErrorFormat(
                                "[ASYNC REQUEST]: Request {0} {1} failed with status {2} and message {3}",
                                verb, requestUrl, e.Status, e.Message);
                        }
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat(
                            "[ASYNC REQUEST]: Request {0} {1} failed with exception {2}{3}",
                            verb, requestUrl, e.Message, e.StackTrace);
                    }
    
                    //  m_log.DebugFormat("[ASYNC REQUEST]: Received {0}", deserial.ToString());

                    try
                    {
                        action(deserial);
                    }
                    catch (Exception e)
                    {
                        m_log.ErrorFormat(
                            "[ASYNC REQUEST]: Request {0} {1} callback failed with exception {2}{3}",
                            verb, requestUrl, e.Message, e.StackTrace);
                    }
    
                }, null);
            }

            int tickdiff = Util.EnvironmentTickCountSubtract(tickstart);
            if (tickdiff > WebUtil.LongCallTime)
            {
                string originalRequest = null;

                if (buffer != null)
                {
                    originalRequest = Encoding.UTF8.GetString(buffer.ToArray());

                    if (originalRequest.Length > WebUtil.MaxRequestDiagLength)
                        originalRequest = originalRequest.Remove(WebUtil.MaxRequestDiagLength);
                }

                m_log.InfoFormat(
                    "[ASYNC REQUEST]: Slow request {0} {1} {2} took {3}ms, {4}ms writing, {5}",
                    reqnum,
                    verb,
                    requestUrl,
                    tickdiff,
                    tickdata,
                    originalRequest);
            }
            else if (WebUtil.DebugLevel >= 4)
            {
                m_log.DebugFormat(
                    "[WEB UTIL]: HTTP OUT {0} took {1}ms, {2}ms writing",
                    reqnum, tickdiff, tickdata);
            }
        }
    }

    public static class SynchronousRestFormsRequester
    {
        /// <summary>
        /// Perform a synchronous REST request.
        /// </summary>
        /// <param name="verb"></param>
        /// <param name="requestUrl"></param>
        /// <param name="obj"> </param>
        /// <param name="timeoutsecs"> </param>
        /// <returns></returns>
        ///
        /// <exception cref="System.Net.WebException">Thrown if we encounter a network issue while posting
        /// the request.  You'll want to make sure you deal with this as they're not uncommon</exception>
        public static string MakeRequest(string verb, string requestUrl, string obj, int timeoutsecs)
        {
            return SynchronousHttpRequester.MakeRequest (verb, requestUrl, "application/x-www-form-urlencoded", obj, timeoutsecs);
        }

        public static string MakeRequest(string verb, string requestUrl, string obj)
        {
            return MakeRequest(verb, requestUrl, obj, -1);
        }
    }

    public class SynchronousRestObjectRequester
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(
            MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Perform a synchronous REST request.
        /// </summary>
        /// <param name="verb"></param>
        /// <param name="requestUrl"></param>
        /// <param name="obj"> </param>
        /// <returns></returns>
        ///
        /// <exception cref="System.Net.WebException">Thrown if we encounter a network issue while posting
        /// the request.  You'll want to make sure you deal with this as they're not uncommon</exception>
        public static TResponse MakeRequest<TRequest, TResponse>(string verb, string requestUrl, TRequest obj)
        {
            return MakeRequest<TRequest, TResponse>(verb, requestUrl, obj, 0);
        }

        public static TResponse MakeRequest<TRequest, TResponse>(string verb, string requestUrl, TRequest obj, int pTimeout)
        {
            return MakeRequest<TRequest, TResponse>(verb, requestUrl, obj, pTimeout, 0);
        }

        public static TResponse MakeRequest<TRequest, TResponse>(string verb, string requestUrl, TRequest obj, int pTimeout, int maxConnections)
        {
            string contentType = "application/x-www-form-urlencoded";

            byte[] bytes = null;
            if ((verb == "POST") || (verb == "PUT")) {
                using (MemoryStream buffer = new MemoryStream()) {

                    XmlWriterSettings settings = new XmlWriterSettings();
                    settings.Encoding = Encoding.UTF8;

                    using (XmlWriter writer = XmlWriter.Create(buffer, settings)) {
                        XmlSerializer serializer = new XmlSerializer (typeof (TRequest));
                        serializer.Serialize(writer, obj);
                        writer.Flush();
                    }

                    bytes = buffer.ToArray ();
                }

                contentType = "text/xml";
            }

            TResponse deserial = default (TResponse);
            try {
                bytes = SynchronousHttpRequester.MakeRequest (verb, requestUrl, contentType, bytes, pTimeout);
                using (Stream respStream = new MemoryStream (bytes)) {
                    XmlSerializer deserializer = new XmlSerializer (typeof (TResponse));
                    deserial = (TResponse)deserializer.Deserialize(respStream);
                }
            } catch (InvalidOperationException e) {
                m_log.Error ("[SynchronousRestObjectRequester]: InvalidXML for " + verb + " " + requestUrl + " " + typeof(TResponse), e);
            } catch (Exception e) {
                m_log.Error ("[SynchronousRestObjectRequester]: Exception for " + verb + " " + requestUrl + " " + typeof(TResponse), e);
            }

            return deserial;
        }
    }

    public class SynchronousHttpRequester {
        private static readonly ILog m_log = LogManager.GetLogger (MethodBase.GetCurrentMethod().DeclaringType);

        /**
         * @brief Send Http(s) request and read response.
         * @param verb = GET, POST, PUT
         * @param requestUrl = web address
         * @param obj = string containing any post data
         * @param timeoutsecs = number of seconds for timeout
         * @returns string containing reply
         */
        public static string MakeRequest(string verb, string requestUrl, string contentType, string obj, int timeoutsecs)
        {
            byte[] bytes = (obj == null) ? null : Encoding.UTF8.GetBytes (obj);
            bytes = MakeRequest (verb, requestUrl, contentType, bytes, timeoutsecs);
            return Encoding.UTF8.GetString (bytes);
        }

        public static byte[] MakeRequest(string verb, string requestUrl, string contentType, byte[] obj, int timeoutsecs)
        {
            int reqnum = WebUtil.RequestNumber ++;
            int tickstart = Environment.TickCount;

            /*
             * Pick apart the given URL and make sure we support it.
             */
            Uri uri = new Uri (requestUrl);
            bool https = uri.Scheme == "https";
            if (!https && (uri.Scheme != "http")) {
                throw new WebException ("only support http and https, not " + uri.Scheme);
            }
            string host = uri.Host;
            int port = uri.Port;
            if (port < 0) port = https ? 443 : 80;
            string path = uri.AbsolutePath;

            /*
             * Connect to the web server.
             */
            System.Net.Sockets.TcpClient tcpconnection = new System.Net.Sockets.TcpClient (host, port);
            if (timeoutsecs > 0) {
                tcpconnection.SendTimeout    = timeoutsecs * 1000;
                tcpconnection.ReceiveTimeout = timeoutsecs * 1000;
            }

            try {

                /*
                 * Get TCP stream to/from web server.
                 * If HTTPS, wrap stream with SSL encryption.
                 */
                Stream tcpstream = tcpconnection.GetStream ();
                if (https) {
                    System.Net.Security.SslStream sslstream = new System.Net.Security.SslStream (tcpstream, false);
                    sslstream.AuthenticateAsClient (host);
                    tcpstream = sslstream;
                }

                /*
                 * Write request header to the web server.
                 * There might be some POST data as well to write to web server.
                 */
                WriteStream (tcpstream, verb + " " + path + " HTTP/1.1\r\n");
                WriteStream (tcpstream, "Host: " + host + "\r\n");
                if (obj != null) {
                    WriteStream (tcpstream, "Content-Length: " + obj.Length + "\r\n");
                    WriteStream (tcpstream, "Content-Type: " + contentType + "\r\n");
                    WriteStream (tcpstream, "\r\n");
                    tcpstream.Write (obj, 0, obj.Length);
                } else {
                    WriteStream (tcpstream, "\r\n");
                }
                tcpstream.Flush ();

                /*
                 * Check for successful reply status line.
                 */
                string headerline = ReadStreamLine (tcpstream).Trim ();
                if (headerline != "HTTP/1.1 200 OK") throw new WebException ("status line " + headerline);

                /*
                 * Scan through header lines.
                 * The only ones we care about are Content-Length and Transfer-Encoding.
                 */
                bool chunked = false;
                int contentlength = -1;
                while ((headerline = ReadStreamLine (tcpstream).Trim ().ToLowerInvariant ()) != "") {
                    if (headerline.StartsWith ("content-length:")) {
                        contentlength = int.Parse (headerline.Substring (15));
                    }
                    if (headerline.StartsWith ("transfer-encoding:") && (headerline.Substring (18).Trim () == "chunked")) {
                        chunked = true;
                    }
                }

                /*
                 * Read response byte array as a series of chunks.
                 */
                byte[] respbytes;
                if (chunked) {
                    LinkedList<byte[]> chunks = new LinkedList<byte[]> ();
                    contentlength = 0;
                    int chunklen;
                    do {
                        chunklen = int.Parse (ReadStreamLine (tcpstream), System.Globalization.NumberStyles.HexNumber);
                        byte[] chunk = new byte[chunklen];
                        int lenread;
                        for (int offs = 0; offs < chunklen; offs += lenread) {
                            lenread = tcpstream.Read (chunk, offs, chunklen - offs);
                            if (lenread <= 0) throw new WebException ("end of stream");
                        }
                        int b = tcpstream.ReadByte ();
                        if (b == '\r') b = tcpstream.ReadByte ();
                        if (b != '\n') throw new WebException ("chunk not followed by \\r\\n");
                        chunks.AddLast (chunk);
                        contentlength += chunklen;
                    } while (chunklen > 0);
                    respbytes = new byte[contentlength];
                    contentlength = 0;
                    foreach (byte[] chunk in chunks) {
                        Array.Copy (chunk, 0, respbytes, contentlength, chunk.Length);
                        contentlength += chunk.Length;
                    }
                }

                /*
                 * Read response byte array with the exact length given by Content-Length.
                 */
                else if (contentlength >= 0) {
                    respbytes = new byte[contentlength];
                    int lenread;
                    for (int offs = 0; offs < contentlength; offs += lenread) {
                        lenread = tcpstream.Read (respbytes, offs, contentlength - offs);
                        if (lenread <= 0) throw new WebException ("end of stream");
                    }
                }

                /*
                 * Don't know how it is being transferred.
                 */
                else {
                    throw new WebException ("header missing content-length or transfer-encoding: chunked");
                }

                /*
                 * Output warning message if request took a long time.
                 */
                int tickdiff = Environment.TickCount - tickstart;
                if (tickdiff > WebUtil.LongCallTime) {
                    m_log.InfoFormat(
                        "[SynchronousHttpRequester.MakeRequest]: Slow request {0} {1} {2} took {3}ms",
                        reqnum, verb, requestUrl, tickdiff);
                }

                /*
                 * Return response byte array.
                 */
                return respbytes;
            } finally {
                tcpconnection.Close ();
            }
        }

        /**
         * @brief Write the string out as ASCII bytes.
         */
        private static void WriteStream (Stream stream, string line)
        {
            byte[] bytes = Encoding.ASCII.GetBytes (line);
            stream.Write (bytes, 0, bytes.Length);
        }

        /**
         * @brief Read the next text line from a stream.
         * @returns string with \r\n trimmed off
         */
        private static string ReadStreamLine (Stream stream)
        {
            StringBuilder sb = new StringBuilder ();
            while (true) {
                int b = stream.ReadByte ();
                if (b < 0) break;
                if (b == '\n') break;
                if (b == '\r') continue;
                sb.Append ((char)b);
            }
            return sb.ToString ();
        }
    }
}
