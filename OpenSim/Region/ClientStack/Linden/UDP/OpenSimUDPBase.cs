/*
 * Copyright (c) 2006, Clutch, Inc.
 * Original Author: Jeff Cesnik
 * All rights reserved.
 *
 * - Redistribution and use in source and binary forms, with or without 
 *   modification, are permitted provided that the following conditions are met:
 *
 * - Redistributions of source code must retain the above copyright notice, this
 *   list of conditions and the following disclaimer.
 * - Neither the name of the openmetaverse.org nor the names 
 *   of its contributors may be used to endorse or promote products derived from
 *   this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" 
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE 
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE 
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE 
 * LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR 
 * CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF 
 * SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN 
 * CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) 
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE 
 * POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using log4net;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;

namespace OpenMetaverse
{
    /// <summary>
    /// Base UDP server
    /// </summary>
    public abstract class OpenSimUDPBase
    {
        private static readonly ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// This method is called when an incoming packet is received
        /// </summary>
        /// <param name="buffer">Incoming packet buffer</param>
        public abstract void PacketReceived(UDPPacketBuffer buffer);

        /// <summary>UDP port to bind to in server mode</summary>
        protected int m_udpPort;

        /// <summary>Local IP address to bind to in server mode</summary>
        protected IPAddress m_localBindAddress;

        /// <summary>UDP socket, used in either client or server mode</summary>
        private Socket m_udpSocket;

        /// <summary>Flag to process packets asynchronously or synchronously</summary>
        private bool m_asyncPacketHandling;

        /// <summary>
        /// Are we to use object pool(s) to reduce memory churn when receiving data?
        /// </summary>
        public bool UsePools { get; protected set; }

        /// <summary>
        /// Pool to use for handling data.  May be null if UsePools = false;
        /// </summary>
        protected OpenSim.Framework.Pool<UDPPacketBuffer> Pool { get; private set; }

        /// <summary>Returns true if the server is currently listening for inbound packets, otherwise false</summary>
        public bool IsRunningInbound { get; private set; }

        /// <summary>Returns true if the server is currently sending outbound packets, otherwise false</summary>
        /// <remarks>If IsRunningOut = false, then any request to send a packet is simply dropped.</remarks>
        public bool IsRunningOutbound { get; private set; }

        /// <summary>
        /// Number of UDP receives.
        /// </summary>
        public int UdpReceives { get; private set; }

        /// <summary>
        /// Number of UDP sends
        /// </summary>
        public int UdpSends { get; private set; }

        /// <summary>
        /// Number of receives over which to establish a receive time average.
        /// </summary>
        private readonly static int s_receiveTimeSamples = 500;

        /// <summary>
        /// Current number of samples taken to establish a receive time average.
        /// </summary>
        private int m_currentReceiveTimeSamples;

        /// <summary>
        /// Cumulative receive time for the sample so far.
        /// </summary>
        private int m_receiveTicksInCurrentSamplePeriod;

        /// <summary>
        /// The average time taken for each require receive in the last sample.
        /// </summary>
        public float AverageReceiveTicksForLastSamplePeriod { get; private set; }

        #region PacketDropDebugging
        /// <summary>
        /// For debugging purposes only... random number generator for dropping
        /// outbound packets.
        /// </summary>
        private Random m_dropRandomGenerator = new Random();
        
        /// <summary>
        /// For debugging purposes only... parameters for a simplified
        /// model of packet loss with bursts, overall drop rate should
        /// be roughly 1 - m_dropLengthProbability / (m_dropProbabiliy + m_dropLengthProbability)
        /// which is about 1% for parameters 0.0015 and 0.15
        /// </summary>
        private double m_dropProbability = 0.0030;
        private double m_dropLengthProbability = 0.15;
        private bool m_dropState = false;

        /// <summary>
        /// For debugging purposes only... parameters to control the time
        /// duration over which packet loss bursts can occur, if no packets
        /// have been sent for m_dropResetTicks milliseconds, then reset the
        /// state of the packet dropper to its default.
        /// </summary>
        private int m_dropLastTick = 0;
        private int m_dropResetTicks = 500;
        
        /// <summary>
        /// Debugging code used to simulate dropped packets with bursts
        /// </summary>
        private bool DropOutgoingPacket()
        {
            double rnum = m_dropRandomGenerator.NextDouble();

            // if the connection has been idle for awhile (more than m_dropResetTicks) then
            // reset the state to the default state, don't continue a burst
            int curtick = Util.EnvironmentTickCount();
            if (Util.EnvironmentTickCountSubtract(curtick, m_dropLastTick) > m_dropResetTicks)
                m_dropState = false;

            m_dropLastTick = curtick;

            // if we are dropping packets, then the probability of dropping
            // this packet is the probability that we stay in the burst
            if (m_dropState)
            {
                m_dropState = (rnum < (1.0 - m_dropLengthProbability)) ? true : false;
            }
            else
            {
                m_dropState = (rnum < m_dropProbability) ? true : false;
            }

            return m_dropState;
        }
        #endregion PacketDropDebugging

        /// <summary>
        /// Default constructor
        /// </summary>
        /// <param name="bindAddress">Local IP address to bind the server to</param>
        /// <param name="port">Port to listening for incoming UDP packets on</param>
        /// /// <param name="usePool">Are we to use an object pool to get objects for handing inbound data?</param>
        public OpenSimUDPBase(IPAddress bindAddress, int port)
        {
            m_localBindAddress = bindAddress;
            m_udpPort = port;

            // for debugging purposes only, initializes the random number generator
            // used for simulating packet loss
            // m_dropRandomGenerator = new Random();
        }

        /// <summary>
        /// Start inbound UDP packet handling.
        /// </summary>
        /// <param name="recvBufferSize">The size of the receive buffer for 
        /// the UDP socket. This value is passed up to the operating system 
        /// and used in the system networking stack. Use zero to leave this
        /// value as the default</param>
        /// <param name="asyncPacketHandling">Set this to true to start
        /// receiving more packets while current packet handler callbacks are
        /// still running. Setting this to false will complete each packet
        /// callback before the next packet is processed</param>
        /// <remarks>This method will attempt to set the SIO_UDP_CONNRESET flag
        /// on the socket to get newer versions of Windows to behave in a sane
        /// manner (not throwing an exception when the remote side resets the
        /// connection). This call is ignored on Mono where the flag is not
        /// necessary</remarks>
        public virtual void StartInbound(int recvBufferSize, bool asyncPacketHandling)
        {
            m_asyncPacketHandling = asyncPacketHandling;

            if (!IsRunningInbound)
            {
                m_log.DebugFormat("[UDPBASE]: Starting inbound UDP loop");

                const int SIO_UDP_CONNRESET = -1744830452;

                IPEndPoint ipep = new IPEndPoint(m_localBindAddress, m_udpPort);

                m_udpSocket = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Dgram,
                    ProtocolType.Udp);

                // OpenSim may need this but in AVN, this messes up automated
                // sim restarts badly
                //m_udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);

                try
                {
                    if (m_udpSocket.Ttl < 128)
                    {
                        m_udpSocket.Ttl = 128;
                    }
                }
                catch (SocketException)
                {
                    m_log.Debug("[UDPBASE]: Failed to increase default TTL");
                }
                try
                {
                    // This udp socket flag is not supported under mono, 
                    // so we'll catch the exception and continue
                    m_udpSocket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
                    m_log.Debug("[UDPBASE]: SIO_UDP_CONNRESET flag set");
                }
                catch (SocketException)
                {
                    m_log.Debug("[UDPBASE]: SIO_UDP_CONNRESET flag not supported on this platform, ignoring");
                }

                // On at least Mono 3.2.8, multiple UDP sockets can bind to the same port by default.  At the moment
                // we never want two regions to listen on the same port as they cannot demultiplex each other's messages,
                // leading to a confusing bug.
                // By default, Windows does not allow two sockets to bind to the same port.
                m_udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);

                if (recvBufferSize != 0)
                    m_udpSocket.ReceiveBufferSize = recvBufferSize;

                m_udpSocket.Bind(ipep);

                IsRunningInbound = true;

                // kick off an async receive.  The Start() method will return, the
                // actual receives will occur asynchronously and will be caught in
                // AsyncEndRecieve().
                new Thread (ReceiveThread).Start ();
            }
        }

        /// <summary>
        /// Start outbound UDP packet handling.
        /// </summary>
        public virtual void StartOutbound()
        {
            m_log.DebugFormat("[UDPBASE]: Starting outbound UDP loop");

            IsRunningOutbound = true;
        }

        public virtual void StopInbound()
        {
            if (IsRunningInbound)
            {
                m_log.DebugFormat("[UDPBASE]: Stopping inbound UDP loop");

                IsRunningInbound = false;
                m_udpSocket.Close();
            }
        }

        public virtual void StopOutbound()
        {
            m_log.DebugFormat("[UDPBASE]: Stopping outbound UDP loop");

            IsRunningOutbound = false;
        }

        public virtual bool EnablePools()
        {
            if (!UsePools)
            {
                Pool = new Pool<UDPPacketBuffer>(() => new UDPPacketBuffer(), 500);

                UsePools = true;

                return true;
            }

            return false;
        }

        public virtual bool DisablePools()
        {
            if (UsePools)
            {
                UsePools = false;

                // We won't null out the pool to avoid a race condition with code that may be in the middle of using it.

                return true;
            }

            return false;
        }

        private void ReceiveThread ()
        {
            bool salvaging = false;

            while (IsRunningInbound) {
                int len = 0;
                UDPPacketBuffer buf = new UDPPacketBuffer();
                try
                {
                    len = m_udpSocket.ReceiveFrom (buf.Data, 0, UDPPacketBuffer.BUFFER_SIZE, SocketFlags.None, ref buf.RemoteEndPoint);
                    if (salvaging) {
                        m_log.Warn("[UDPBASE]: Salvaged the UDP listener on port " + m_udpPort);
                        salvaging = false;
                    }
                }
                catch (SocketException e)
                {
                    if (!salvaging && (e.SocketErrorCode == SocketError.ConnectionReset)) {
                        m_log.Warn("[UDPBASE]: SIO_UDP_CONNRESET was ignored, attempting to salvage the UDP listener on port " + m_udpPort);
                        salvaging = true;
                    }
                }
                catch (ObjectDisposedException) { }
                if (len > 0) {
                    buf.DataLength = len;
                    PacketReceived (buf);
                }
            }
        }

        public void AsyncBeginSend(UDPPacketBuffer buf)
        {
//            if (IsRunningOutbound)
//            {

                // This is strictly for debugging purposes to simulate dropped
                // packets when testing throttles & retransmission code
                // if (DropOutgoingPacket())
                //     return;
            
                try
                {
                    m_udpSocket.SendTo (buf.Data, 0, buf.DataLength, SocketFlags.None, buf.RemoteEndPoint);

                    FakeUdpSendResult result = new FakeUdpSendResult ();
                    result.asyncState = buf;
                    m_udpSocket.EndSendTo(result);
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
//            }
        }

        private class FakeUdpSendResult : IAsyncResult {
            public object asyncState;
            public object AsyncState { get { return asyncState; } }
            public WaitHandle AsyncWaitHandle { get { return null; } }
            public bool CompletedSynchronously { get { return true; } }
            public bool IsCompleted { get { return true; } }
        }
    }
}
