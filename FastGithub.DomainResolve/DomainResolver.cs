﻿using FastGithub.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 域名解析器
    /// </summary> 
    sealed class DomainResolver : IDomainResolver
    {
        const int MAX_ADDRESS_COUNT = 4;
        private readonly DnsClient dnsClient;
        private readonly DomainPersistence persistence;
        private readonly ILogger<DomainResolver> logger;
        private readonly ConcurrentDictionary<DnsEndPoint, IPAddressElapsed[]> dnsEndPointAddressElapseds = new();

        /// <summary>
        /// 域名解析器
        /// </summary>
        /// <param name="dnsClient"></param>
        /// <param name="persistence"></param>
        /// <param name="logger"></param>
        public DomainResolver(
            DnsClient dnsClient,
            DomainPersistence persistence,
            ILogger<DomainResolver> logger)
        {
            this.dnsClient = dnsClient;
            this.persistence = persistence;
            this.logger = logger;

            foreach (var endPoint in persistence.ReadDnsEndPoints())
            {
                this.dnsEndPointAddressElapseds.TryAdd(endPoint, Array.Empty<IPAddressElapsed>());
            }
        }


        /// <summary>
        /// 解析ip
        /// </summary>
        /// <param name="endPoint">节点</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<IPAddress> ResolveAnyAsync(DnsEndPoint endPoint, CancellationToken cancellationToken = default)
        {
            await foreach (var address in this.ResolveAllAsync(endPoint, cancellationToken))
            {
                return address;
            }
            throw new FastGithubException($"解析不到{endPoint.Host}的IP");
        }

        /// <summary>
        /// 解析域名
        /// </summary>
        /// <param name="endPoint">节点</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async IAsyncEnumerable<IPAddress> ResolveAllAsync(DnsEndPoint endPoint, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (this.dnsEndPointAddressElapseds.TryGetValue(endPoint, out var addressElapseds) && addressElapseds.Length > 0)
            {
                foreach (var addressElapsed in addressElapseds)
                {
                    yield return addressElapsed.Adddress;
                }
            }
            else
            {
                if (this.dnsEndPointAddressElapseds.TryAdd(endPoint, Array.Empty<IPAddressElapsed>()))
                {
                    await this.persistence.WriteDnsEndPointsAsync(this.dnsEndPointAddressElapseds.Keys, cancellationToken);
                }

                await foreach (var adddress in this.dnsClient.ResolveAsync(endPoint, fastSort: true, cancellationToken))
                {
                    yield return adddress;
                }
            }
        }

        /// <summary>
        /// 对所有节点进行测速
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task TestAllEndPointsAsync(CancellationToken cancellationToken)
        {
            foreach (var keyValue in this.dnsEndPointAddressElapseds)
            {
                var oldValues = keyValue.Value;
                if (oldValues.Length >= MAX_ADDRESS_COUNT)
                {
                    if (oldValues.Any(item => item.NeedUpdateElapsed()) == false)
                    {
                        continue;
                    }
                }

                var dnsEndPoint = keyValue.Key;
                var hashSet = new HashSet<IPAddressElapsed>(oldValues);
                await foreach (var adddress in this.dnsClient.ResolveAsync(dnsEndPoint, fastSort: false, cancellationToken))
                {
                    hashSet.Add(new IPAddressElapsed(adddress, dnsEndPoint.Port));
                }

                // 两个以上才进行测速排序
                if (hashSet.Count > 1)
                {
                    var updateTasks = hashSet
                        .Where(item => item.NeedUpdateElapsed())
                        .Select(item => item.UpdateElapsedAsync(cancellationToken));
                    await Task.WhenAll(updateTasks);
                }

                var newValues = hashSet
                    .Where(item => item.Elapsed < TimeSpan.MaxValue)
                    .OrderBy(item => item.Elapsed)
                    .Take(count: MAX_ADDRESS_COUNT)
                    .ToArray();

                if (oldValues.SequenceEqual(newValues) == false)
                {
                    this.dnsEndPointAddressElapseds[dnsEndPoint] = newValues;

                    var addressArray = string.Join(", ", newValues.Select(item => item.ToString()));
                    this.logger.LogInformation($"{dnsEndPoint.Host}->[{addressArray}]");
                }
            }
        }
    }
}
