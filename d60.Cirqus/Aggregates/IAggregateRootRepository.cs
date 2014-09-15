﻿using System;
using d60.Cirqus.Events;

namespace d60.Cirqus.Aggregates
{
    /// <summary>
    /// Repository of aggregate roots.
    /// </summary>
    public interface IAggregateRootRepository
    {
        /// <summary>
        /// Returns a fully hydrated and ready to use aggregate root instance of the specified type. Optionally, if <seealso cref="maxGlobalSequenceNumber"/> is set,
        /// only events up until (and including) the specified sequence number are applied.
        /// </summary>
        AggregateRootInfo<TAggregate> Get<TAggregate>(Guid aggregateRootId, IUnitOfWork unitOfWork, long maxGlobalSequenceNumber = long.MaxValue) where TAggregate : AggregateRoot, new();

        /// <summary>
        /// Checks whether an aggregate root of the specified type with the specified ID exists. Optionally, if <seealso cref="maxGlobalSequenceNumber"/> is set,
        /// it is checked whether the root exists at the given point in time (including the specified sequence number)
        /// </summary>
        bool Exists<TAggregate>(Guid aggregateRootId, long maxGlobalSequenceNumber = long.MaxValue, IUnitOfWork unitOfWork = null) where TAggregate : AggregateRoot;
    }
}