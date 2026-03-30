using CodeReviewAgent.Models;

namespace CodeReviewAgent.Tests.Evals.EvalFixtures;

/// <summary>
/// Golden code samples used in eval tests.
/// Each sample has known characteristics so we can assert expected agent behavior.
/// </summary>
public static class GoldenCodeSamples
{
    /// <summary>
    /// C# file with multiple well-known security vulnerabilities:
    ///   - Hardcoded database credentials in a const string (~line 11)
    ///   - SQL injection via string concatenation in GetUserByUsername (~line 22)
    ///   - Second SQL injection + missing authorization in DeleteUser (~line 35)
    ///
    /// Expected eval outcome: several HIGH-severity comments.
    /// </summary>
    public static PullRequestFile SecurityVulnerableCSharp => new()
    {
        Path = "/src/Data/UserRepository.cs",
        ChangeType = "add",
        Content = SecurityVulnerableContent,
        UnifiedDiff = SecurityVulnerableDiff,
        PreviousContent = string.Empty
    };

    /// <summary>
    /// Clean, idiomatic C# following SOLID, proper async, DI, and logging.
    /// No intentional issues.
    ///
    /// Expected eval outcome: 0-2 minor/nitpick comments at most.
    /// Used to verify the agent does NOT hallucinate issues on good code.
    /// </summary>
    public static PullRequestFile CleanCSharp => new()
    {
        Path = "/src/Services/ProductService.cs",
        ChangeType = "add",
        Content = CleanContent,
        UnifiedDiff = CleanDiff,
        PreviousContent = string.Empty
    };

    // -------------------------------------------------------------------------
    // Security-vulnerable fixture
    // -------------------------------------------------------------------------

    private const string SecurityVulnerableContent = """
        using System.Data.SqlClient;
        using Microsoft.Extensions.Logging;

        namespace Data
        {
            public class UserRepository
            {
                private readonly ILogger<UserRepository> _logger;

                // Hardcoded production credentials - critical security vulnerability
                private const string ConnectionString =
                    "Server=prod-db.internal;Database=UsersDb;User Id=sa;Password=<REDACTED_FOR_SCAN>;";

                public UserRepository(ILogger<UserRepository> logger)
                {
                    _logger = logger;
                }

                public User? GetUserByUsername(string username)
                {
                    using var connection = new SqlConnection(ConnectionString);
                    connection.Open();

                    // SQL injection: raw string concatenation with user input
                    var query = "SELECT * FROM Users WHERE Username = '" + username + "'";
                    using var command = new SqlCommand(query, connection);

                    using var reader = command.ExecuteReader();
                    if (reader.Read())
                    {
                        return new User
                        {
                            Id = (int)reader["Id"],
                            Username = (string)reader["Username"],
                            Email = (string)reader["Email"]
                        };
                    }
                    return null;
                }

                public void DeleteUser(string userId)
                {
                    // No authorization check + SQL injection via userId
                    using var connection = new SqlConnection(ConnectionString);
                    connection.Open();
                    var query = "DELETE FROM Users WHERE Id = " + userId;
                    using var command = new SqlCommand(query, connection);
                    command.ExecuteNonQuery();
                }
            }

            public record User(int Id = 0, string Username = "", string Email = "");
        }
        """;

    private const string SecurityVulnerableDiff = """
        @@ -0,0 +1,52 @@
        +using System.Data.SqlClient;
        +using Microsoft.Extensions.Logging;
        +
        +namespace Data
        +{
        +    public class UserRepository
        +    {
        +        private readonly ILogger<UserRepository> _logger;
        +
        +        // Hardcoded production credentials - critical security vulnerability
        +        private const string ConnectionString =
        +            "Server=prod-db.internal;Database=UsersDb;User Id=sa;Password=<REDACTED_FOR_SCAN>;";
        +
        +        public UserRepository(ILogger<UserRepository> logger)
        +        {
        +            _logger = logger;
        +        }
        +
        +        public User? GetUserByUsername(string username)
        +        {
        +            using var connection = new SqlConnection(ConnectionString);
        +            connection.Open();
        +
        +            // SQL injection: raw string concatenation with user input
        +            var query = "SELECT * FROM Users WHERE Username = '" + username + "'";
        +            using var command = new SqlCommand(query, connection);
        +
        +            using var reader = command.ExecuteReader();
        +            if (reader.Read())
        +            {
        +                return new User
        +                {
        +                    Id = (int)reader["Id"],
        +                    Username = (string)reader["Username"],
        +                    Email = (string)reader["Email"]
        +                };
        +            }
        +            return null;
        +        }
        +
        +        public void DeleteUser(string userId)
        +        {
        +            // No authorization check + SQL injection via userId
        +            using var connection = new SqlConnection(ConnectionString);
        +            connection.Open();
        +            var query = "DELETE FROM Users WHERE Id = " + userId;
        +            using var command = new SqlCommand(query, connection);
        +            command.ExecuteNonQuery();
        +        }
        +    }
        +
        +    public record User(int Id = 0, string Username = "", string Email = "");
        +}
        """;

    // -------------------------------------------------------------------------
    // Clean code fixture
    // -------------------------------------------------------------------------

    private const string CleanContent = """
        using Microsoft.Extensions.Logging;

        namespace Services
        {
            public interface IProductService
            {
                Task<Product?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
                Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken cancellationToken = default);
            }

            public class ProductService : IProductService
            {
                private readonly IProductRepository _repository;
                private readonly ILogger<ProductService> _logger;

                public ProductService(
                    IProductRepository repository,
                    ILogger<ProductService> logger)
                {
                    _repository = repository ?? throw new ArgumentNullException(nameof(repository));
                    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
                }

                public async Task<Product?> GetByIdAsync(
                    int id,
                    CancellationToken cancellationToken = default)
                {
                    if (id <= 0)
                        throw new ArgumentOutOfRangeException(nameof(id), "Product ID must be positive.");

                    _logger.LogInformation("Fetching product {ProductId}", id);
                    return await _repository
                        .GetByIdAsync(id, cancellationToken)
                        .ConfigureAwait(false);
                }

                public async Task<IReadOnlyList<Product>> GetAllAsync(
                    CancellationToken cancellationToken = default)
                {
                    _logger.LogInformation("Fetching all products");
                    return await _repository
                        .GetAllAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        """;

    private const string CleanDiff = """
        @@ -0,0 +1,46 @@
        +using Microsoft.Extensions.Logging;
        +
        +namespace Services
        +{
        +    public interface IProductService
        +    {
        +        Task<Product?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
        +        Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken cancellationToken = default);
        +    }
        +
        +    public class ProductService : IProductService
        +    {
        +        private readonly IProductRepository _repository;
        +        private readonly ILogger<ProductService> _logger;
        +
        +        public ProductService(
        +            IProductRepository repository,
        +            ILogger<ProductService> logger)
        +        {
        +            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        +            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        +        }
        +
        +        public async Task<Product?> GetByIdAsync(
        +            int id,
        +            CancellationToken cancellationToken = default)
        +        {
        +            if (id <= 0)
        +                throw new ArgumentOutOfRangeException(nameof(id), "Product ID must be positive.");
        +
        +            _logger.LogInformation("Fetching product {ProductId}", id);
        +            return await _repository
        +                .GetByIdAsync(id, cancellationToken)
        +                .ConfigureAwait(false);
        +        }
        +
        +        public async Task<IReadOnlyList<Product>> GetAllAsync(
        +            CancellationToken cancellationToken = default)
        +        {
        +            _logger.LogInformation("Fetching all products");
        +            return await _repository
        +                .GetAllAsync(cancellationToken)
        +                .ConfigureAwait(false);
        +        }
        +    }
        +}
        """;
}
