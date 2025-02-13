﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rebus.Messages;
using Rebus.Pipeline;
using Rebus.Pipeline.Receive;
using Rebus.Transport;
// ReSharper disable ForCanBeConvertedToForeach

namespace Rebus.Sagas.Exclusive;

abstract class EnforceExclusiveSagaAccessIncomingStepBase : IIncomingStep
{
    readonly SagaHelper _sagaHelper = new SagaHelper();
    protected readonly int _maxLockBuckets;
    protected readonly CancellationToken _cancellationToken;

    protected EnforceExclusiveSagaAccessIncomingStepBase(int maxLockBuckets, CancellationToken cancellationToken)
    {
        _maxLockBuckets = maxLockBuckets;
        _cancellationToken = cancellationToken;
    }

    public async Task Process(IncomingStepContext context, Func<Task> next)
    {
        var handlerInvokersForSagas = context.Load<HandlerInvokers>()
            .Where(l => l.HasSaga)
            .ToList();

        if (!handlerInvokersForSagas.Any())
        {
            await next();
            return;
        }

        var message = context.Load<Message>();
        var transactionContext = context.Load<ITransactionContext>();
        var messageContext = new MessageContext(transactionContext);
        var body = message.Body;

        var correlationProperties = handlerInvokersForSagas
            .Select(h => h.Saga)
            .SelectMany(saga => _sagaHelper.GetCorrelationProperties(saga).ForMessage(body)
                .Select(correlationProperty => (saga, correlationProperty)))
            .ToList();

        var locksToObtain = correlationProperties
            .Select(a => new
            {
                SagaDataType = a.saga.GetSagaDataType().FullName,
                CorrelationPropertyName = a.correlationProperty.PropertyName,
                CorrelationPropertyValue = a.correlationProperty.GetValueFromMessage(messageContext, message)
            })
            .Select(a => a.ToString())
            .Select(lockId => GetHashCodeStable(lockId) % _maxLockBuckets)
            .Distinct() // avoid accidentally acquiring the same lock twice, because a bucket got hit more than once
            .OrderBy(str => str) // enforce consistent ordering to avoid deadlocks
            .ToArray();

        var obtainedLocks = new List<int>();
		try
		{
		    await WaitForLocks(locksToObtain, obtainedLocks);
		
		    await next();
		}
		finally
		{
	        await ReleaseLocks(obtainedLocks.ToArray());
		}
    }

    /// <summary>
    /// Cannot use <see cref="object.GetHashCode"/>, because it's not stable across processes in modern .NET
    /// </summary>
    static int GetHashCodeStable(string lockId)
    {
        using var hasher = MD5.Create();

        var hash = hasher.ComputeHash(Encoding.UTF8.GetBytes(lockId));

        return Math.Abs(BitConverter.ToInt32(hash, startIndex: 0));
    }

    protected abstract Task<bool> AcquireLockAsync(int lockId);

    protected abstract Task<bool> ReleaseLockAsync(int lockId);

    async Task WaitForLocks(int[] lockIds, List<int> obtainedLockIds)
    {
        for (var index = 0; index < lockIds.Length; index++)
        {
            while (!await AcquireLockAsync(lockIds[index]))
            {
                await Task.Yield();
            }
            obtainedLockIds.Add(lockIds[index]);
        }
    }

    async Task ReleaseLocks(int[] lockIds)
    {
        for (var index = 0; index < lockIds.Length; index++)
        {
            await ReleaseLockAsync(lockIds[index]);
        }
    }
}