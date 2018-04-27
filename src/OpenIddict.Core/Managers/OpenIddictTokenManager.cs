﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/openiddict/openiddict-core for more information concerning
 * the license and the contributors participating to this project.
 */

using System;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;

namespace OpenIddict.Core
{
    /// <summary>
    /// Provides methods allowing to manage the tokens stored in the store.
    /// </summary>
    /// <typeparam name="TToken">The type of the Token entity.</typeparam>
    public class OpenIddictTokenManager<TToken> where TToken : class
    {
        public OpenIddictTokenManager(
            [NotNull] IOpenIddictTokenStoreResolver resolver,
            [NotNull] ILogger<OpenIddictTokenManager<TToken>> logger,
            [NotNull] IOptions<OpenIddictCoreOptions> options)
        {
            Store = resolver.Get<TToken>();
            Logger = logger;
            Options = options;
        }

        /// <summary>
        /// Gets the logger associated with the current manager.
        /// </summary>
        protected ILogger Logger { get; }

        /// <summary>
        /// Gets the options associated with the current manager.
        /// </summary>
        protected IOptions<OpenIddictCoreOptions> Options { get; }

        /// <summary>
        /// Gets the store associated with the current manager.
        /// </summary>
        protected IOpenIddictTokenStore<TToken> Store { get; }

        /// <summary>
        /// Determines the number of tokens that exist in the database.
        /// </summary>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the number of tokens in the database.
        /// </returns>
        public virtual Task<long> CountAsync(CancellationToken cancellationToken = default)
        {
            return Store.CountAsync(cancellationToken);
        }

        /// <summary>
        /// Determines the number of tokens that match the specified query.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="query">The query to execute.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the number of tokens that match the specified query.
        /// </returns>
        public virtual Task<long> CountAsync<TResult>(
            [NotNull] Func<IQueryable<TToken>, IQueryable<TResult>> query, CancellationToken cancellationToken = default)
        {
            if (query == null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            return Store.CountAsync(query, cancellationToken);
        }

        /// <summary>
        /// Creates a new token.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        public virtual async Task CreateAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            // If no status was explicitly specified, assume that the token is valid.
            if (string.IsNullOrEmpty(await Store.GetStatusAsync(token, cancellationToken)))
            {
                await Store.SetStatusAsync(token, OpenIddictConstants.Statuses.Valid, cancellationToken);
            }

            var results = await ValidateAsync(token, cancellationToken);
            if (results.Any(result => result != ValidationResult.Success))
            {
                throw new ValidationException(results.FirstOrDefault(result => result != ValidationResult.Success), null, token);
            }

            await Store.CreateAsync(token, cancellationToken);
        }

        /// <summary>
        /// Creates a new token based on the specified descriptor.
        /// </summary>
        /// <param name="descriptor">The token descriptor.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation, whose result returns the token.
        /// </returns>
        public virtual async Task<TToken> CreateAsync(
            [NotNull] OpenIddictTokenDescriptor descriptor, CancellationToken cancellationToken = default)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            var token = await Store.InstantiateAsync(cancellationToken);
            if (token == null)
            {
                throw new InvalidOperationException("An error occurred while trying to create a new token");
            }

            await PopulateAsync(token, descriptor, cancellationToken);
            await CreateAsync(token, cancellationToken);

            return token;
        }

        /// <summary>
        /// Removes an existing token.
        /// </summary>
        /// <param name="token">The token to delete.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        public virtual Task DeleteAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            return Store.DeleteAsync(token, cancellationToken);
        }

        /// <summary>
        /// Extends the specified token by replacing its expiration date.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="date">The date on which the token will no longer be considered valid.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        public virtual async Task ExtendAsync([NotNull] TToken token,
            [CanBeNull] DateTimeOffset? date, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            await Store.SetExpirationDateAsync(token, date, cancellationToken);
            await UpdateAsync(token, cancellationToken);
        }

        /// <summary>
        /// Retrieves the list of tokens corresponding to the specified application identifier.
        /// </summary>
        /// <param name="identifier">The application identifier associated with the tokens.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the tokens corresponding to the specified application.
        /// </returns>
        public virtual Task<ImmutableArray<TToken>> FindByApplicationIdAsync(
            [NotNull] string identifier, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                throw new ArgumentException("The identifier cannot be null or empty.", nameof(identifier));
            }

            return Store.FindByApplicationIdAsync(identifier, cancellationToken);
        }

        /// <summary>
        /// Retrieves the list of tokens corresponding to the specified authorization identifier.
        /// </summary>
        /// <param name="identifier">The authorization identifier associated with the tokens.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the tokens corresponding to the specified authorization.
        /// </returns>
        public virtual Task<ImmutableArray<TToken>> FindByAuthorizationIdAsync(
            [NotNull] string identifier, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                throw new ArgumentException("The identifier cannot be null or empty.", nameof(identifier));
            }

            return Store.FindByAuthorizationIdAsync(identifier, cancellationToken);
        }

        /// <summary>
        /// Retrieves the list of tokens corresponding to the specified reference identifier.
        /// Note: the reference identifier may be hashed or encrypted for security reasons.
        /// </summary>
        /// <param name="identifier">The reference identifier associated with the tokens.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the tokens corresponding to the specified reference identifier.
        /// </returns>
        public virtual async Task<TToken> FindByReferenceIdAsync([NotNull] string identifier, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                throw new ArgumentException("The identifier cannot be null or empty.", nameof(identifier));
            }

            // SQL engines like Microsoft SQL Server or MySQL are known to use case-insensitive lookups by default.
            // To ensure a case-sensitive comparison is enforced independently of the database/table/query collation
            // used by the store, a second pass using string.Equals(StringComparison.Ordinal) is manually made here.

            identifier = await ObfuscateReferenceIdAsync(identifier, cancellationToken);

            var token = await Store.FindByReferenceIdAsync(identifier, cancellationToken);
            if (token == null ||
                !string.Equals(await Store.GetReferenceIdAsync(token, cancellationToken), identifier, StringComparison.Ordinal))
            {
                return null;
            }

            return token;
        }

        /// <summary>
        /// Retrieves a token using its unique identifier.
        /// </summary>
        /// <param name="identifier">The unique identifier associated with the token.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the token corresponding to the unique identifier.
        /// </returns>
        public virtual Task<TToken> FindByIdAsync([NotNull] string identifier, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                throw new ArgumentException("The identifier cannot be null or empty.", nameof(identifier));
            }

            return Store.FindByIdAsync(identifier, cancellationToken);
        }

        /// <summary>
        /// Retrieves the list of tokens corresponding to the specified subject.
        /// </summary>
        /// <param name="subject">The subject associated with the tokens.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the tokens corresponding to the specified subject.
        /// </returns>
        public virtual async Task<ImmutableArray<TToken>> FindBySubjectAsync(
            [NotNull] string subject, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(subject))
            {
                throw new ArgumentException("The subject cannot be null or empty.", nameof(subject));
            }

            // SQL engines like Microsoft SQL Server or MySQL are known to use case-insensitive lookups by default.
            // To ensure a case-sensitive comparison is enforced independently of the database/table/query collation
            // used by the store, a second pass using string.Equals(StringComparison.Ordinal) is manually made here.

            var tokens = await Store.FindBySubjectAsync(subject, cancellationToken);
            if (tokens.IsEmpty)
            {
                return ImmutableArray.Create<TToken>();
            }

            var builder = ImmutableArray.CreateBuilder<TToken>(tokens.Length);

            foreach (var token in tokens)
            {
                if (string.Equals(await Store.GetSubjectAsync(token, cancellationToken), subject, StringComparison.Ordinal))
                {
                    builder.Add(token);
                }
            }

            return builder.Count == builder.Capacity ?
                builder.MoveToImmutable() :
                builder.ToImmutable();
        }

        /// <summary>
        /// Retrieves the optional application identifier associated with a token.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the application identifier associated with the token.
        /// </returns>
        public virtual ValueTask<string> GetApplicationIdAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            return Store.GetApplicationIdAsync(token, cancellationToken);
        }

        /// <summary>
        /// Executes the specified query and returns the first element.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="query">The query to execute.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the first element returned when executing the query.
        /// </returns>
        public virtual Task<TResult> GetAsync<TResult>(
            [NotNull] Func<IQueryable<TToken>, IQueryable<TResult>> query, CancellationToken cancellationToken = default)
        {
            return GetAsync((tokens, state) => state(tokens), query, cancellationToken);
        }

        /// <summary>
        /// Executes the specified query and returns the first element.
        /// </summary>
        /// <typeparam name="TState">The state type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="query">The query to execute.</param>
        /// <param name="state">The optional state.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the first element returned when executing the query.
        /// </returns>
        public virtual Task<TResult> GetAsync<TState, TResult>(
            [NotNull] Func<IQueryable<TToken>, TState, IQueryable<TResult>> query,
            [CanBeNull] TState state, CancellationToken cancellationToken = default)
        {
            if (query == null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            return Store.GetAsync(query, state, cancellationToken);
        }

        /// <summary>
        /// Retrieves the optional authorization identifier associated with a token.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the authorization identifier associated with the token.
        /// </returns>
        public virtual ValueTask<string> GetAuthorizationIdAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            return Store.GetAuthorizationIdAsync(token, cancellationToken);
        }

        /// <summary>
        /// Retrieves the creation date associated with a token.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the creation date associated with the specified token.
        /// </returns>
        public virtual ValueTask<DateTimeOffset?> GetCreationDateAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            return Store.GetCreationDateAsync(token, cancellationToken);
        }

        /// <summary>
        /// Retrieves the expiration date associated with a token.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the expiration date associated with the specified token.
        /// </returns>
        public virtual ValueTask<DateTimeOffset?> GetExpirationDateAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            return Store.GetExpirationDateAsync(token, cancellationToken);
        }

        /// <summary>
        /// Retrieves the unique identifier associated with a token.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the unique identifier associated with the token.
        /// </returns>
        public virtual ValueTask<string> GetIdAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            return Store.GetIdAsync(token, cancellationToken);
        }

        /// <summary>
        /// Retrieves the payload associated with a token.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the payload associated with the specified token.
        /// </returns>
        public virtual ValueTask<string> GetPayloadAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            return Store.GetPayloadAsync(token, cancellationToken);
        }

        /// <summary>
        /// Retrieves the reference identifier associated with a token.
        /// Note: depending on the manager used to create the token,
        /// the reference identifier may be hashed for security reasons.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the reference identifier associated with the specified token.
        /// </returns>
        public virtual ValueTask<string> GetReferenceIdAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            return Store.GetReferenceIdAsync(token, cancellationToken);
        }

        /// <summary>
        /// Retrieves the status associated with a token.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the status associated with the specified token.
        /// </returns>
        public virtual ValueTask<string> GetStatusAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            return Store.GetStatusAsync(token, cancellationToken);
        }

        /// <summary>
        /// Retrieves the subject associated with a token.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the subject associated with the specified token.
        /// </returns>
        public virtual ValueTask<string> GetSubjectAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            return Store.GetSubjectAsync(token, cancellationToken);
        }

        /// <summary>
        /// Retrieves the token type associated with a token.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the token type associated with the specified token.
        /// </returns>
        public virtual ValueTask<string> GetTokenTypeAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            return Store.GetTokenTypeAsync(token, cancellationToken);
        }

        /// <summary>
        /// Determines whether a given token has already been redemeed.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns><c>true</c> if the token has already been redemeed, <c>false</c> otherwise.</returns>
        public virtual async Task<bool> IsRedeemedAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            var status = await Store.GetStatusAsync(token, cancellationToken);
            if (string.IsNullOrEmpty(status))
            {
                return false;
            }

            return string.Equals(status, OpenIddictConstants.Statuses.Redeemed, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether a given token has been revoked.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns><c>true</c> if the token has been revoked, <c>false</c> otherwise.</returns>
        public virtual async Task<bool> IsRevokedAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            var status = await Store.GetStatusAsync(token, cancellationToken);
            if (string.IsNullOrEmpty(status))
            {
                return false;
            }

            return string.Equals(status, OpenIddictConstants.Statuses.Revoked, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether a given token is valid.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns><c>true</c> if the token is valid, <c>false</c> otherwise.</returns>
        public virtual async Task<bool> IsValidAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            var status = await Store.GetStatusAsync(token, cancellationToken);
            if (string.IsNullOrEmpty(status))
            {
                return false;
            }

            return string.Equals(status, OpenIddictConstants.Statuses.Valid, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Executes the specified query and returns all the corresponding elements.
        /// </summary>
        /// <param name="count">The number of results to return.</param>
        /// <param name="offset">The number of results to skip.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns all the elements returned when executing the specified query.
        /// </returns>
        public virtual Task<ImmutableArray<TToken>> ListAsync(
            [CanBeNull] int? count, [CanBeNull] int? offset, CancellationToken cancellationToken = default)
        {
            return Store.ListAsync(count, offset, cancellationToken);
        }

        /// <summary>
        /// Executes the specified query and returns all the corresponding elements.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="query">The query to execute.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns all the elements returned when executing the specified query.
        /// </returns>
        public virtual Task<ImmutableArray<TResult>> ListAsync<TResult>(
            [NotNull] Func<IQueryable<TToken>, IQueryable<TResult>> query, CancellationToken cancellationToken = default)
        {
            return ListAsync((tokens, state) => state(tokens), query, cancellationToken);
        }

        /// <summary>
        /// Executes the specified query and returns all the corresponding elements.
        /// </summary>
        /// <typeparam name="TState">The state type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="query">The query to execute.</param>
        /// <param name="state">The optional state.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns all the elements returned when executing the specified query.
        /// </returns>
        public virtual Task<ImmutableArray<TResult>> ListAsync<TState, TResult>(
            [NotNull] Func<IQueryable<TToken>, TState, IQueryable<TResult>> query,
            [CanBeNull] TState state, CancellationToken cancellationToken = default)
        {
            if (query == null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            return Store.ListAsync(query, state, cancellationToken);
        }

        /// <summary>
        /// Obfuscates the specified reference identifier so it can be safely stored in a database.
        /// By default, this method returns a simple hashed representation computed using SHA256.
        /// </summary>
        /// <param name="identifier">The client identifier.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        public virtual Task<string> ObfuscateReferenceIdAsync([NotNull] string identifier, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                throw new ArgumentException("The identifier cannot be null or empty.", nameof(identifier));
            }

            using (var algorithm = SHA256.Create())
            {
                // Compute the digest of the generated identifier and use it as the hashed identifier of the reference token.
                // Doing that prevents token identifiers stolen from the database from being used as valid reference tokens.
                return Task.FromResult(Convert.ToBase64String(algorithm.ComputeHash(Encoding.UTF8.GetBytes(identifier))));
            }
        }

        /// <summary>
        /// Removes the tokens that are marked as expired or invalid.
        /// </summary>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        public virtual Task PruneAsync(CancellationToken cancellationToken = default)
            => Store.PruneAsync(cancellationToken);

        /// <summary>
        /// Redeems a token.
        /// </summary>
        /// <param name="token">The token to redeem.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>A <see cref="Task"/> that can be used to monitor the asynchronous operation.</returns>
        public virtual async Task RedeemAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            var status = await Store.GetStatusAsync(token, cancellationToken);
            if (!string.Equals(status, OpenIddictConstants.Statuses.Redeemed, StringComparison.OrdinalIgnoreCase))
            {
                await Store.SetStatusAsync(token, OpenIddictConstants.Statuses.Redeemed, cancellationToken);
                await UpdateAsync(token, cancellationToken);
            }
        }

        /// <summary>
        /// Revokes a token.
        /// </summary>
        /// <param name="token">The token to revoke.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>A <see cref="Task"/> that can be used to monitor the asynchronous operation.</returns>
        public virtual async Task RevokeAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            var status = await Store.GetStatusAsync(token, cancellationToken);
            if (!string.Equals(status, OpenIddictConstants.Statuses.Revoked, StringComparison.OrdinalIgnoreCase))
            {
                await Store.SetStatusAsync(token, OpenIddictConstants.Statuses.Revoked, cancellationToken);
                await UpdateAsync(token, cancellationToken);
            }
        }

        /// <summary>
        /// Sets the application identifier associated with a token.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="identifier">The unique identifier associated with the client application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        public virtual async Task SetApplicationIdAsync([NotNull] TToken token,
            [CanBeNull] string identifier, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            await Store.SetApplicationIdAsync(token, identifier, cancellationToken);
            await UpdateAsync(token, cancellationToken);
        }

        /// <summary>
        /// Sets the authorization identifier associated with a token.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="identifier">The unique identifier associated with the authorization.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        public virtual async Task SetAuthorizationIdAsync([NotNull] TToken token,
            [CanBeNull] string identifier, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            await Store.SetAuthorizationIdAsync(token, identifier, cancellationToken);
            await UpdateAsync(token, cancellationToken);
        }

        /// <summary>
        /// Updates an existing token.
        /// </summary>
        /// <param name="token">The token to update.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        public virtual async Task UpdateAsync([NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            var results = await ValidateAsync(token, cancellationToken);
            if (results.Any(result => result != ValidationResult.Success))
            {
                throw new ValidationException(results.FirstOrDefault(result => result != ValidationResult.Success), null, token);
            }

            await Store.UpdateAsync(token, cancellationToken);
        }

        /// <summary>
        /// Updates an existing token.
        /// </summary>
        /// <param name="token">The token to update.</param>
        /// <param name="operation">The delegate used to update the token based on the given descriptor.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        public virtual async Task UpdateAsync([NotNull] TToken token,
            [NotNull] Func<OpenIddictTokenDescriptor, Task> operation, CancellationToken cancellationToken = default)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            var descriptor = new OpenIddictTokenDescriptor
            {
                ApplicationId = await Store.GetApplicationIdAsync(token, cancellationToken),
                AuthorizationId = await Store.GetAuthorizationIdAsync(token, cancellationToken),
                CreationDate = await Store.GetCreationDateAsync(token, cancellationToken),
                ExpirationDate = await Store.GetExpirationDateAsync(token, cancellationToken),
                Payload = await Store.GetPayloadAsync(token, cancellationToken),
                ReferenceId = await Store.GetReferenceIdAsync(token, cancellationToken),
                Status = await Store.GetStatusAsync(token, cancellationToken),
                Subject = await Store.GetSubjectAsync(token, cancellationToken),
                Type = await Store.GetTokenTypeAsync(token, cancellationToken)
            };

            await operation(descriptor);
            await PopulateAsync(token, descriptor, cancellationToken);
            await UpdateAsync(token, cancellationToken);
        }

        /// <summary>
        /// Validates the token to ensure it's in a consistent state.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the validation error encountered when validating the token.
        /// </returns>
        public virtual async Task<ImmutableArray<ValidationResult>> ValidateAsync(
            [NotNull] TToken token, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            var builder = ImmutableArray.CreateBuilder<ValidationResult>();

            // If a reference identifier was associated with the token,
            // ensure it's not already used for a different token.
            var identifier = await Store.GetReferenceIdAsync(token, cancellationToken);
            if (!string.IsNullOrEmpty(identifier))
            {
                // Note: depending on the database/table/query collation used by the store, a reference token
                // whose identifier doesn't exactly match the specified value may be returned (e.g because
                // the casing is different). To avoid issues when the reference identifier is part of an index
                // using the same collation, an error is added even if the two identifiers don't exactly match.
                var other = await Store.FindByReferenceIdAsync(identifier, cancellationToken);
                if (other != null && !string.Equals(
                    await Store.GetIdAsync(other, cancellationToken),
                    await Store.GetIdAsync(token, cancellationToken), StringComparison.Ordinal))
                {
                    builder.Add(new ValidationResult("A token with the same reference identifier already exists."));
                }
            }

            var type = await Store.GetTokenTypeAsync(token, cancellationToken);
            if (string.IsNullOrEmpty(type))
            {
                builder.Add(new ValidationResult("The token type cannot be null or empty."));
            }

            else if (!string.Equals(type, OpenIddictConstants.TokenTypes.AccessToken, StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(type, OpenIddictConstants.TokenTypes.AuthorizationCode, StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(type, OpenIddictConstants.TokenTypes.RefreshToken, StringComparison.OrdinalIgnoreCase))
            {
                builder.Add(new ValidationResult("The specified token type is not supported by the default token manager."));
            }

            if (string.IsNullOrEmpty(await Store.GetStatusAsync(token, cancellationToken)))
            {
                builder.Add(new ValidationResult("The status cannot be null or empty."));
            }

            if (string.IsNullOrEmpty(await Store.GetSubjectAsync(token, cancellationToken)))
            {
                builder.Add(new ValidationResult("The subject cannot be null or empty."));
            }

            return builder.Count == builder.Capacity ?
                builder.MoveToImmutable() :
                builder.ToImmutable();
        }

        /// <summary>
        /// Populates the token using the specified descriptor.
        /// </summary>
        /// <param name="token">The token.</param>
        /// <param name="descriptor">The descriptor.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        protected virtual async Task PopulateAsync([NotNull] TToken token,
            [NotNull] OpenIddictTokenDescriptor descriptor, CancellationToken cancellationToken = default)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            await Store.SetApplicationIdAsync(token, descriptor.ApplicationId, cancellationToken);
            await Store.SetAuthorizationIdAsync(token, descriptor.AuthorizationId, cancellationToken);
            await Store.SetCreationDateAsync(token, descriptor.CreationDate, cancellationToken);
            await Store.SetExpirationDateAsync(token, descriptor.ExpirationDate, cancellationToken);
            await Store.SetPayloadAsync(token, descriptor.Payload, cancellationToken);
            await Store.SetReferenceIdAsync(token, descriptor.ReferenceId, cancellationToken);
            await Store.SetStatusAsync(token, descriptor.Status, cancellationToken);
            await Store.SetSubjectAsync(token, descriptor.Subject, cancellationToken);
            await Store.SetTokenTypeAsync(token, descriptor.Type, cancellationToken);
        }
    }
}