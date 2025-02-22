﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

/// <summary>
/// A bare-bones, pooled builder, focused on the case of producing <see cref="ImmutableArray{T}"/>s where the final
/// array size is known at construction time.  In the golden path, where all the expected items are added to the
/// builder, and <see cref="MoveToImmutable"/> is called, this type is entirely garbage free.  In the non-golden path
/// (usually encountered when a cancellation token interrupts getting the final array), this will leak the intermediary
/// array created to store the results.
/// </summary>
[NonCopyable]
internal struct FixedSizeArrayBuilder<T>(int capacity)
{
    private T[] _values = new T[capacity];
    private int _index;

    public void Add(T value)
        => _values[_index++] = value;

    /// <summary>
    /// Moves the underlying buffer out of control of this type, into the returned <see cref="ImmutableArray{T}"/>. It
    /// is an error for a client of this type to specify a capacity and then attempt to call <see
    /// cref="MoveToImmutable"/> without that number of elements actually having been added to the builder.  This will
    /// throw if attempted.  This <see cref="FixedSizeArrayBuilder{T}"/> is effectively unusable once this is called.
    /// The internal buffer will reset to an empty array, meaning no more items could ever be added to it.
    /// </summary>
    public ImmutableArray<T> MoveToImmutable()
    {
        Contract.ThrowIfTrue(_index != _values.Length);
        var result = ImmutableCollectionsMarshal.AsImmutableArray(_values);
        _values = Array.Empty<T>();
        _index = 0;
        return result;
    }
}
