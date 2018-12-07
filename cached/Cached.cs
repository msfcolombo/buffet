/*
Copyright (c) 2018 Fernando Colombo

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Threading;

namespace Buffet
{
    public sealed class Cached<T> where T : class
    {
        public delegate CachedItem<T> OnCreate( CachedItem<T> current, DateTime requestDate, TimeSpan? maxAge );

        public delegate void OnEvict( CachedItem<T> item );

        private readonly string _name;
        private readonly OnCreate _onCreate;
        private readonly CachedOnCreateCallMode _callMode;
        private readonly OnEvict _onEvict;

        public Cached(
            OnEvict onEvict = null
        )
        {
            _onEvict = onEvict;
        }

        public Cached(
            OnCreate onCreate,
            CachedOnCreateCallMode callMode = CachedOnCreateCallMode.SingleThread,
            OnEvict onEvict = null
        )
        {
            _onCreate = onCreate ?? throw new ArgumentNullException( nameof( onCreate ) );
            _callMode = callMode;
            _onEvict = onEvict;
        }

        public TimeSpan? MaxAge { get; set; }

        private CachedItem<T> _current;

        public CachedItem<T> Current => Interlocked.CompareExchange( ref _current, null, null );

        public void Set( T value, TimeSpan? maxAge )
        {
            Set( new CachedItem<T>( value, DateTime.UtcNow, maxAge ?? MaxAge ) );
        }

        public void Set( CachedItem<T> item )
        {
            var previous = Replace( item );
            if ( previous != null && previous != item )
                _onEvict?.Invoke( previous );
        }

        public CachedItem<T> Replace( CachedItem<T> item )
        {
            return Interlocked.Exchange( ref _current, item );
        }

        public T Get( TimeSpan? maxAge = null )
        {
            var item = GetItem( maxAge );
            if ( item != null )
                return item.Value;

            if ( _onCreate != null )
                throw new InvalidOperationException( "Unexpected state." );

            throw new CacheException( "Item is null or expired, and a creator was not specified." );
        }

        public T Peek( TimeSpan? maxAge = null )
        {
            var current = Current;

            if ( current != null && !current.IsExpired( maxAge ) )
                return current.Value;

            return null;
        }

        public CachedItem<T> GetItem( TimeSpan? maxAge = null )
        {
            var current = Current;

            if ( current != null && !current.IsExpired( maxAge ) )
                return current;

            // If a creator was not specified, return null.
            if ( _onCreate == null )
            {
                // We don't clear _current here. If the user wants to clear, they can simply call Set(null) or Replace(null).
                return null;
            }

            var requestDate = DateTime.UtcNow;

            // Let's call the creator according to the call mode.
            // Any exception that the creator throws is forwarded with no side effects to this object.
            CachedItem<T> result;
            switch ( _callMode )
            {
                case CachedOnCreateCallMode.SingleThread:
                    lock ( this )
                    {
                        result = _onCreate( current, requestDate, MaxAge );
                    }

                    break;

                case CachedOnCreateCallMode.Concurrent:
                    result = _onCreate( current, requestDate, MaxAge );
                    break;

                default:
                    throw new InvalidOperationException( $"Unexpected value at {nameof( _callMode )}: {_callMode}." );
            }

            // The creator must never return null. Either throw an exception or return a non-null value.
            if ( result == null )
                throw new CacheException( "Bad implementation of creator method: returned null." );

            // The creator may return the current value. No change is required in this case.
            if ( result == current )
                return result;

            for ( ;; )
            {
                // Try to use the object returned by the creator.
                var previous = Interlocked.CompareExchange( ref _current, result, current );
                if ( previous == current )
                {
                    // The change was effective (this thread won the race).

                    // Evict the previous entry, if any.
                    if ( previous != null )
                        _onEvict?.Invoke( previous );

                    // Return the new entry.
                    return result;
                }

                // This thread lost the race. Check if the winner used a non-null value.
                if ( previous != null )
                {
                    // Evict our brand-new item.
                    _onEvict?.Invoke( result );

                    // Return the one that won the race.
                    return previous;
                }

                // Someone set _current to null. We still have a chance to use our item. Keep looping.
                current = previous;
            }
        }
    }

    public enum CachedOnCreateCallMode
    {
        SingleThread,
        Concurrent
    }

    public sealed class CacheException : Exception
    {
        public CacheException( string message ) : base( message )
        {
        }

        public CacheException( string message, Exception innerException ) : base( message, innerException )
        {
        }
    }

    public sealed class CachedItem<T> where T : class
    {
        private readonly T _value;
        private readonly DateTime _requestDate;
        private readonly TimeSpan? _maxAge;

        public CachedItem( T value, DateTime requestDate, TimeSpan? maxAge )
        {
            if ( requestDate.Kind != DateTimeKind.Utc )
                throw new ArgumentException( $"{nameof( requestDate )} must be UTC, but {requestDate.Kind} was specified." );

            _value = value;
            _requestDate = requestDate;
            _maxAge = maxAge;
        }

        public DateTime? GetExpireTime( TimeSpan? maxAge = null )
        {
            maxAge = maxAge ?? _maxAge;
            return _requestDate + maxAge;
        }

        public TimeSpan? GetAgeLeft( TimeSpan? maxAge = null )
        {
            return GetExpireTime( maxAge ) - DateTime.UtcNow;
        }

        public bool IsExpired( TimeSpan? maxAge = null )
        {
            maxAge = maxAge ?? _maxAge;
            return maxAge != null && Age > maxAge;
        }

        public T Value => _value;
        public DateTime RequestDate => _requestDate;
        public TimeSpan? MaxAge => _maxAge;
        public TimeSpan Age => DateTime.UtcNow - _requestDate;
    }
}
