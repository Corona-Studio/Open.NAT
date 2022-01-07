//
// Authors:
//   Ben Motmans <ben.motmans@gmail.com>
//   Lucas Ontivero lucas.ontivero@gmail.com
//
// Copyright (C) 2007 Ben Motmans
// Copyright (C) 2014 Lucas Ontivero
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Open.Nat;

internal sealed class PmpNatDevice : NatDevice
{
	public override IPEndPoint HostEndPoint { get; }

	public override IPAddress LocalAddress { get; }
	private IPAddress PublicAddress { get; }

	internal PmpNatDevice(IPAddress hostEndPointAddress, IPAddress localAddress, IPAddress publicAddress)
	{
		HostEndPoint = new IPEndPoint(hostEndPointAddress, PmpConstants.ServerPort);
		LocalAddress = localAddress;
		PublicAddress = publicAddress;
	}

	public override async Task CreatePortMapAsync(Mapping mapping)
	{
		await InternalCreatePortMapAsync(mapping, true)
			.TimeoutAfterAsync(TimeSpan.FromSeconds(4));
		RegisterMapping(mapping);
	}

	public override async Task DeletePortMapAsync(Mapping mapping)
	{
		await InternalCreatePortMapAsync(mapping, false)
			.TimeoutAfterAsync(TimeSpan.FromSeconds(4));
		UnregisterMapping(mapping);
	}

	public override Task<IEnumerable<Mapping>> GetAllMappingsAsync()
	{
		throw new NotSupportedException();
	}

	public override Task<IPAddress> GetExternalIPAsync()
	{
		return Task.Run(() => PublicAddress)
			.TimeoutAfterAsync(TimeSpan.FromSeconds(4));
	}

	public override Task<Mapping> GetSpecificMappingAsync(Protocol protocol, int port)
	{
		throw new NotSupportedException("NAT-PMP does not specify a way to get a specific port map");
	}

	private async Task<Mapping> InternalCreatePortMapAsync(Mapping mapping, bool create)
	{
		var package = new List<byte>();

		package.Add(PmpConstants.Version);
		package.Add(mapping.Protocol == Protocol.Tcp ? PmpConstants.OperationCodeTcp : PmpConstants.OperationCodeUdp);
		package.Add(0); //reserved
		package.Add(0); //reserved
		package.AddRange(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)mapping.PrivatePort)));
		package.AddRange(
			BitConverter.GetBytes(create ? IPAddress.HostToNetworkOrder((short)mapping.PublicPort) : (short)0));
		package.AddRange(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(mapping.Lifetime)));

		try
		{
			byte[] buffer = package.ToArray();
			int attempt = 0;
			int delay = PmpConstants.RetryDelay;

			using var udpClient = new UdpClient();
			CreatePortMapListen(udpClient, mapping);

			while (attempt < PmpConstants.RetryAttempts)
			{
				await
					udpClient.SendAsync(buffer, buffer.Length, HostEndPoint);

				attempt++;
				delay *= 2;
				Thread.Sleep(delay);
			}
		}
		catch (Exception e)
		{
			string type = create ? "create" : "delete";
			string message = $"Failed to {type} portmap (protocol={mapping.Protocol}, private port={mapping.PrivatePort})";
			NatDiscoverer.TraceSource.LogError(message);
			var pmpException = e as MappingException;
			throw new MappingException(message, pmpException);
		}

		return mapping;
	}

	private void CreatePortMapListen(UdpClient udpClient, Mapping mapping)
	{
		var endPoint = HostEndPoint;

		while (true)
		{
			byte[] data = udpClient.Receive(ref endPoint);

			if (data.Length < 16)
				continue;

			if (data[0] != PmpConstants.Version)
				continue;

			var opCode = (byte)(data[1] & 127);

			var protocol = Protocol.Tcp;
			if (opCode == PmpConstants.OperationCodeUdp)
				protocol = Protocol.Udp;

			short resultCode = IPAddress.NetworkToHostOrder(BitConverter.ToInt16(data, 2));
			int epoch = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(data, 4));

			short privatePort = IPAddress.NetworkToHostOrder(BitConverter.ToInt16(data, 8));
			short publicPort = IPAddress.NetworkToHostOrder(BitConverter.ToInt16(data, 10));

			var lifetime = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(data, 12));

			if (privatePort < 0 || publicPort < 0 || resultCode != PmpConstants.ResultCodeSuccess)
			{
				var errors = new[]
								 {
										 "Success",
										 "Unsupported Version",
										 "Not Authorized/Refused (e.g. box supports mapping, but user has turned feature off)"
										 ,
										 "Network Failure (e.g. NAT box itself has not obtained a DHCP lease)",
										 "Out of resources (NAT box cannot create any more mappings at this time)",
										 "Unsupported opcode"
									 };
				throw new MappingException(resultCode, errors[resultCode]);
			}

			if (lifetime == 0) return; //mapping was deleted

			//mapping was created
			//TODO: verify that the private port+protocol are a match
			mapping.PublicPort = publicPort;
			mapping.Protocol = protocol;
			mapping.Expiration = DateTime.Now.AddSeconds(lifetime);
			return;
		}
	}

	public override string ToString()
	{
		return $"Local Address: {HostEndPoint.Address}\nPublic IP: {PublicAddress}\nLast Seen: {LastSeen}";
	}
}
