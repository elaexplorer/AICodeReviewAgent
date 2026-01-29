using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Services;

/// <summary>
/// Utility for inspecting and understanding vector embeddings
/// Demonstrates how embeddings work internally with real examples
/// </summary>
public class EmbeddingInspector
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ILogger<EmbeddingInspector> _logger;

    public EmbeddingInspector(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ILogger<EmbeddingInspector> logger)
    {
        _embeddingGenerator = embeddingGenerator;
        _logger = logger;
    }

    /// <summary>
    /// Generate and inspect embeddings for sample texts to understand how they work
    /// </summary>
    public async Task DemonstrateEmbeddingsAsync()
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                    EMBEDDING DEMONSTRATION                         ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Sample code snippets that should be semantically similar or different
        var codesamples = new Dictionary<string, string>
        {
            ["Auth_Login"] = """
                public async Task<bool> ValidateUserLogin(string username, string password)
                {
                    var user = await _userService.FindByUsernameAsync(username);
                    if (user == null) return false;
                    
                    return await _passwordService.VerifyPasswordAsync(user.Id, password);
                }
                """,

            ["Auth_JWT"] = """
                public string GenerateJwtToken(User user)
                {
                    var claims = new List<Claim>
                    {
                        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                        new(ClaimTypes.Email, user.Email)
                    };
                    
                    var token = new JwtSecurityToken(
                        issuer: _jwtSettings.Issuer,
                        audience: _jwtSettings.Audience,
                        claims: claims,
                        expires: DateTime.UtcNow.AddHours(24),
                        signingCredentials: _signingCredentials);
                    
                    return new JwtSecurityTokenHandler().WriteToken(token);
                }
                """,

            ["Database_Connection"] = """
                public async Task<IDbConnection> CreateDatabaseConnectionAsync()
                {
                    var connection = new SqlConnection(_connectionString);
                    await connection.OpenAsync();
                    
                    _logger.LogInformation("Database connection established");
                    return connection;
                }
                """,

            ["HTTP_Client"] = """
                public async Task<HttpResponseMessage> SendRequestAsync(string url, object data)
                {
                    using var client = new HttpClient();
                    var json = JsonSerializer.Serialize(data);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    
                    return await client.PostAsync(url, content);
                }
                """,

            ["Auth_Permissions"] = """
                public async Task<bool> CheckUserPermissions(int userId, string resource, string action)
                {
                    var user = await _userRepository.GetByIdAsync(userId);
                    if (user == null) return false;
                    
                    var permissions = await _permissionService.GetUserPermissionsAsync(userId);
                    return permissions.Any(p => p.Resource == resource && p.Actions.Contains(action));
                }
                """
        };

        // Generate embeddings for all samples
        var embeddings = new Dictionary<string, float[]>();
        
        foreach (var (name, code) in codesamples)
        {
            Console.WriteLine($"🔧 Generating embedding for: {name}");
            Console.WriteLine($"📝 Code preview: {code.Substring(0, Math.Min(100, code.Length)).Replace('\n', ' ')}...");
            
            try
            {
                var response = await _embeddingGenerator.GenerateAsync(code);
                var vector = response.Vector.ToArray();
                embeddings[name] = vector;
                
                Console.WriteLine($"✅ Generated {vector.Length}-dimensional vector");
                Console.WriteLine($"   Range: [{vector.Min():F6} to {vector.Max():F6}]");
                Console.WriteLine($"   Mean: {vector.Average():F6}, Std: {CalculateStandardDeviation(vector):F6}");
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Failed: {ex.Message}");
                Console.WriteLine();
            }
        }

        if (embeddings.Any())
        {
            await AnalyzeEmbeddingRelationshipsAsync(embeddings);
            await InspectVectorDimensionsAsync(embeddings);
            await DemonstrateSearchMechanicsAsync(embeddings);
        }
    }

    /// <summary>
    /// Analyze relationships between different code embeddings
    /// </summary>
    private async Task AnalyzeEmbeddingRelationshipsAsync(Dictionary<string, float[]> embeddings)
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                    SIMILARITY ANALYSIS                             ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        var names = embeddings.Keys.ToArray();
        
        // Calculate similarity matrix
        Console.WriteLine("🔍 Cosine Similarity Matrix:");
        Console.WriteLine();
        
        // Header
        Console.Write($"{"",15}");
        foreach (var name in names)
        {
            Console.Write($"{name,12}");
        }
        Console.WriteLine();
        Console.WriteLine(new string('─', 15 + names.Length * 12));

        // Matrix
        foreach (var name1 in names)
        {
            Console.Write($"{name1,-15}");
            foreach (var name2 in names)
            {
                var similarity = CosineSimilarity(embeddings[name1], embeddings[name2]);
                var color = similarity > 0.8 ? "🟢" : similarity > 0.6 ? "🟡" : "🔴";
                Console.Write($"{similarity,9:F4}{color,3}");
            }
            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine("Expected relationships:");
        Console.WriteLine("🟢 High similarity (>0.8): Auth_Login ↔ Auth_JWT ↔ Auth_Permissions (all authentication)");
        Console.WriteLine("🟡 Medium similarity (0.6-0.8): Related but different domains");
        Console.WriteLine("🔴 Low similarity (<0.6): Completely different functionality");
        Console.WriteLine();

        // Find most and least similar pairs
        var similarities = new List<(string name1, string name2, double similarity)>();
        for (int i = 0; i < names.Length; i++)
        {
            for (int j = i + 1; j < names.Length; j++)
            {
                var sim = CosineSimilarity(embeddings[names[i]], embeddings[names[j]]);
                similarities.Add((names[i], names[j], sim));
            }
        }

        var mostSimilar = similarities.OrderByDescending(s => s.similarity).First();
        var leastSimilar = similarities.OrderBy(s => s.similarity).First();

        Console.WriteLine($"🏆 Most similar: {mostSimilar.name1} ↔ {mostSimilar.name2} (similarity: {mostSimilar.similarity:F4})");
        Console.WriteLine($"💥 Least similar: {leastSimilar.name1} ↔ {leastSimilar.name2} (similarity: {leastSimilar.similarity:F4})");
        Console.WriteLine();
    }

    /// <summary>
    /// Inspect the internal structure of embedding vectors
    /// </summary>
    private async Task InspectVectorDimensionsAsync(Dictionary<string, float[]> embeddings)
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                    VECTOR DIMENSION ANALYSIS                       ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        var firstEmbedding = embeddings.Values.First();
        var dimension = firstEmbedding.Length;

        Console.WriteLine($"📐 Vector Dimension: {dimension}");
        Console.WriteLine($"📊 Data Type: {firstEmbedding.GetType().GetElementType()?.Name} (32-bit floating point)");
        Console.WriteLine($"💾 Memory per vector: {dimension * sizeof(float)} bytes ({dimension * sizeof(float) / 1024.0:F2} KB)");
        Console.WriteLine();

        // Analyze value distributions across dimensions
        Console.WriteLine("🔍 Analyzing value patterns across dimensions...");
        Console.WriteLine();

        var sampleVector = embeddings["Auth_Login"];
        
        // Show actual values for first 20 dimensions
        Console.WriteLine("First 20 dimensional values:");
        for (int i = 0; i < Math.Min(20, sampleVector.Length); i++)
        {
            var value = sampleVector[i];
            var magnitude = Math.Abs(value);
            var bar = new string('█', (int)(magnitude * 20)); // Visual representation
            Console.WriteLine($"  Dim {i,3}: {value,8:F6} |{bar,-20}|");
        }
        Console.WriteLine($"  ... {sampleVector.Length - 20} more dimensions");
        Console.WriteLine();

        // Statistical analysis across all embeddings
        Console.WriteLine("📈 Statistical Analysis Across All Vectors:");
        
        var allValues = embeddings.Values.SelectMany(v => v).ToArray();
        var sortedValues = allValues.OrderBy(v => v).ToArray();
        
        Console.WriteLine($"   Total values: {allValues.Length:N0} ({embeddings.Count} vectors × {dimension} dimensions)");
        Console.WriteLine($"   Min value: {allValues.Min():F6}");
        Console.WriteLine($"   Max value: {allValues.Max():F6}");
        Console.WriteLine($"   Mean: {allValues.Average():F6}");
        Console.WriteLine($"   Median: {sortedValues[sortedValues.Length / 2]:F6}");
        Console.WriteLine($"   Std Dev: {CalculateStandardDeviation(allValues):F6}");
        Console.WriteLine();

        // Value distribution
        var histogram = CreateHistogram(allValues, 10);
        Console.WriteLine("Value Distribution (histogram):");
        foreach (var (range, count) in histogram)
        {
            var percentage = (double)count / allValues.Length * 100;
            var bar = new string('▓', (int)(percentage / 2));
            Console.WriteLine($"   {range,20}: {count,8} ({percentage,5:F1}%) |{bar,-50}|");
        }
        Console.WriteLine();
    }

    /// <summary>
    /// Demonstrate how search mechanics work with actual vectors
    /// </summary>
    private async Task DemonstrateSearchMechanicsAsync(Dictionary<string, float[]> embeddings)
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                    SEARCH MECHANICS DEMO                           ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Simulate search queries
        var queries = new[]
        {
            "user authentication and login",
            "database connection setup",
            "HTTP API request handling",
            "security and permissions"
        };

        foreach (var query in queries)
        {
            Console.WriteLine($"🔍 Query: \"{query}\"");
            Console.WriteLine();

            // Generate query embedding
            var queryResponse = await _embeddingGenerator.GenerateAsync(query);
            var queryVector = queryResponse.Vector.ToArray();

            Console.WriteLine($"📐 Query Vector Generated:");
            Console.WriteLine($"   Dimension: {queryVector.Length}");
            Console.WriteLine($"   Range: [{queryVector.Min():F6} to {queryVector.Max():F6}]");
            Console.WriteLine($"   First 10 values: [{string.Join(", ", queryVector.Take(10).Select(v => v.ToString("F6")))}]");
            Console.WriteLine();

            // Calculate similarities
            var results = embeddings.Select(kvp => new
            {
                Name = kvp.Key,
                Vector = kvp.Value,
                Similarity = CosineSimilarity(queryVector, kvp.Value)
            })
            .OrderByDescending(r => r.Similarity)
            .ToArray();

            Console.WriteLine("📊 Search Results (ranked by similarity):");
            foreach (var result in results)
            {
                var relevance = result.Similarity > 0.8 ? "🟢 Highly Relevant" :
                               result.Similarity > 0.6 ? "🟡 Moderately Relevant" :
                               "🔴 Low Relevance";
                               
                Console.WriteLine($"   {result.Similarity:F4} - {result.Name,-20} {relevance}");
            }

            // Explain why this ranking makes sense
            Console.WriteLine();
            Console.WriteLine("💡 Why this ranking:");
            var topResult = results.First();
            Console.WriteLine($"   • '{topResult.Name}' ranks highest because embeddings capture semantic similarity");
            Console.WriteLine($"   • Vector math reveals conceptual relationships beyond keyword matching");
            Console.WriteLine($"   • Similarity {topResult.Similarity:F4} indicates strong semantic alignment");
            Console.WriteLine();
            Console.WriteLine(new string('─', 70));
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Show how cosine similarity calculation works step-by-step
    /// </summary>
    public void DemonstrateCosineCalculation(float[] vectorA, float[] vectorB, string nameA, string nameB)
    {
        Console.WriteLine($"🧮 COSINE SIMILARITY CALCULATION: {nameA} vs {nameB}");
        Console.WriteLine();

        if (vectorA.Length != vectorB.Length)
        {
            Console.WriteLine("❌ Vector dimensions don't match!");
            return;
        }

        // Step-by-step calculation
        Console.WriteLine("Step 1: Calculate dot product (A · B)");
        double dotProduct = 0;
        for (int i = 0; i < Math.Min(10, vectorA.Length); i++) // Show first 10 calculations
        {
            var product = vectorA[i] * vectorB[i];
            dotProduct += product;
            Console.WriteLine($"   Dim {i,2}: {vectorA[i],8:F6} × {vectorB[i],8:F6} = {product,10:F6}");
        }
        
        // Calculate full dot product
        for (int i = 10; i < vectorA.Length; i++)
        {
            dotProduct += vectorA[i] * vectorB[i];
        }
        Console.WriteLine($"   ... {vectorA.Length - 10} more dimensions");
        Console.WriteLine($"   🎯 Total dot product: {dotProduct:F6}");
        Console.WriteLine();

        Console.WriteLine("Step 2: Calculate magnitudes ||A|| and ||B||");
        double magnitudeA = Math.Sqrt(vectorA.Sum(x => x * x));
        double magnitudeB = Math.Sqrt(vectorB.Sum(x => x * x));
        
        Console.WriteLine($"   ||A|| = √(Σ A²) = √({vectorA.Sum(x => x * x):F6}) = {magnitudeA:F6}");
        Console.WriteLine($"   ||B|| = √(Σ B²) = √({vectorB.Sum(x => x * x):F6}) = {magnitudeB:F6}");
        Console.WriteLine();

        Console.WriteLine("Step 3: Calculate cosine similarity");
        var similarity = dotProduct / (magnitudeA * magnitudeB);
        Console.WriteLine($"   Similarity = (A · B) / (||A|| × ||B||)");
        Console.WriteLine($"   Similarity = {dotProduct:F6} / ({magnitudeA:F6} × {magnitudeB:F6})");
        Console.WriteLine($"   🎯 Final Similarity: {similarity:F6}");
        Console.WriteLine();

        // Interpret the result
        var interpretation = similarity > 0.9 ? "Nearly identical semantic meaning" :
                           similarity > 0.8 ? "Very similar concepts" :
                           similarity > 0.7 ? "Related concepts" :
                           similarity > 0.5 ? "Somewhat related" :
                           "Different concepts";
                           
        Console.WriteLine($"📋 Interpretation: {interpretation}");
        Console.WriteLine();
    }

    /// <summary>
    /// Analyze how different types of changes affect embedding similarity
    /// </summary>
    public async Task AnalyzeCodeChangeImpactAsync()
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                 CODE CHANGE IMPACT ANALYSIS                        ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        var originalCode = """
            public class UserService
            {
                public async Task<User> GetUserById(int id)
                {
                    return await _repository.FindAsync(id);
                }
            }
            """;

        var variations = new Dictionary<string, string>
        {
            ["Original"] = originalCode,
            
            ["Rename_Method"] = originalCode.Replace("GetUserById", "FindUserById"),
            
            ["Add_Validation"] = """
                public class UserService
                {
                    public async Task<User> GetUserById(int id)
                    {
                        if (id <= 0) throw new ArgumentException("Invalid user ID");
                        return await _repository.FindAsync(id);
                    }
                }
                """,
                
            ["Change_Logic"] = """
                public class UserService
                {
                    public async Task<User> GetUserById(int id)
                    {
                        var user = await _database.QueryAsync($"SELECT * FROM Users WHERE Id = {id}");
                        return user.FirstOrDefault();
                    }
                }
                """,
                
            ["Different_Domain"] = """
                public class PaymentProcessor
                {
                    public async Task<bool> ProcessPayment(decimal amount, string cardNumber)
                    {
                        var gateway = new PaymentGateway();
                        return await gateway.ChargeAsync(amount, cardNumber);
                    }
                }
                """
        };

        // Generate embeddings for all variations
        var variationEmbeddings = new Dictionary<string, float[]>();
        foreach (var (name, code) in variations)
        {
            var response = await _embeddingGenerator.GenerateAsync(code);
            variationEmbeddings[name] = response.Vector.ToArray();
        }

        var originalVector = variationEmbeddings["Original"];

        Console.WriteLine("📊 Impact of different code changes on embedding similarity:");
        Console.WriteLine();

        foreach (var (name, vector) in variationEmbeddings.Where(kvp => kvp.Key != "Original"))
        {
            var similarity = CosineSimilarity(originalVector, vector);
            var impact = similarity > 0.95 ? "Minimal" :
                        similarity > 0.85 ? "Low" :
                        similarity > 0.70 ? "Moderate" :
                        "High";
                        
            Console.WriteLine($"🔄 {name,-15}: Similarity {similarity:F4} - {impact} impact");
            
            // Show what changed in the vector
            var differences = AnalyzeVectorDifferences(originalVector, vector);
            Console.WriteLine($"     Vector changes: {differences.ChangedDimensions} dims changed, avg change: {differences.AverageChange:F6}");
        }
        Console.WriteLine();
        
        Console.WriteLine("💡 Key Insights:");
        Console.WriteLine("   • Method renames have minimal impact on semantic meaning");
        Console.WriteLine("   • Adding validation preserves core functionality semantics");
        Console.WriteLine("   • Logic changes affect similarity more significantly");
        Console.WriteLine("   • Domain changes (User → Payment) create completely different vectors");
    }

    /// <summary>
    /// Examine what specific dimensions in the vector represent
    /// </summary>
    public async Task ExploreVectorDimensionsAsync()
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                    DIMENSION EXPLORATION                           ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Test concepts to understand what dimensions capture
        var concepts = new[]
        {
            "authentication security login",
            "database connection SQL",
            "HTTP REST API",
            "async await Task",
            "class method function",
            "validation error handling",
            "payment money transaction",
            "file system IO"
        };

        var conceptEmbeddings = new Dictionary<string, float[]>();
        foreach (var concept in concepts)
        {
            var response = await _embeddingGenerator.GenerateAsync(concept);
            conceptEmbeddings[concept] = response.Vector.ToArray();
        }

        // Find dimensions that are consistently high/low for specific concepts
        var dimension = conceptEmbeddings.Values.First().Length;
        
        Console.WriteLine("🔍 Searching for meaningful dimensions...");
        Console.WriteLine("(Dimensions that consistently activate for specific concepts)");
        Console.WriteLine();

        var significantDimensions = new List<(int dim, string concept, double avgActivation)>();

        for (int d = 0; d < Math.Min(100, dimension); d++) // Check first 100 dimensions
        {
            foreach (var (concept, vector) in conceptEmbeddings)
            {
                var activation = Math.Abs(vector[d]);
                if (activation > 0.1) // Significant activation threshold
                {
                    significantDimensions.Add((d, concept, activation));
                }
            }
        }

        var topDimensions = significantDimensions
            .GroupBy(x => x.dim)
            .Where(g => g.Count() >= 2) // Dimensions active in multiple concepts
            .OrderByDescending(g => g.Average(x => x.avgActivation))
            .Take(10);

        Console.WriteLine("🎯 Most Active Dimensions (top 10):");
        foreach (var dimGroup in topDimensions)
        {
            var dim = dimGroup.Key;
            var activeConcepts = dimGroup.Select(x => $"{x.concept} ({x.avgActivation:F3})");
            Console.WriteLine($"   Dimension {dim,3}: {string.Join(", ", activeConcepts)}");
        }
        Console.WriteLine();

        Console.WriteLine("💡 Understanding:");
        Console.WriteLine("   • Each dimension captures different semantic aspects");
        Console.WriteLine("   • High values indicate strong presence of specific concepts");
        Console.WriteLine("   • Patterns emerge: auth-related concepts activate similar dimensions");
        Console.WriteLine("   • 1536 dimensions provide rich, nuanced representation");
    }

    // Helper methods
    private double CosineSimilarity(float[] vectorA, float[] vectorB)
    {
        if (vectorA.Length != vectorB.Length) return 0;

        double dotProduct = 0;
        double magnitudeA = 0;
        double magnitudeB = 0;

        for (int i = 0; i < vectorA.Length; i++)
        {
            dotProduct += vectorA[i] * vectorB[i];
            magnitudeA += vectorA[i] * vectorA[i];
            magnitudeB += vectorB[i] * vectorB[i];
        }

        magnitudeA = Math.Sqrt(magnitudeA);
        magnitudeB = Math.Sqrt(magnitudeB);

        return magnitudeA == 0 || magnitudeB == 0 ? 0 : dotProduct / (magnitudeA * magnitudeB);
    }

    private double CalculateStandardDeviation(IEnumerable<float> values)
    {
        var valueArray = values.ToArray();
        var mean = valueArray.Average();
        var sumOfSquaredDifferences = valueArray.Sum(v => Math.Pow(v - mean, 2));
        return Math.Sqrt(sumOfSquaredDifferences / valueArray.Length);
    }

    private (int ChangedDimensions, double AverageChange) AnalyzeVectorDifferences(float[] vectorA, float[] vectorB)
    {
        var changedDims = 0;
        var totalChange = 0.0;

        for (int i = 0; i < vectorA.Length; i++)
        {
            var difference = Math.Abs(vectorA[i] - vectorB[i]);
            if (difference > 0.001) // Threshold for considering a dimension "changed"
            {
                changedDims++;
                totalChange += difference;
            }
        }

        return (changedDims, changedDims > 0 ? totalChange / changedDims : 0);
    }

    private List<(string range, int count)> CreateHistogram(float[] values, int buckets)
    {
        var min = values.Min();
        var max = values.Max();
        var bucketSize = (max - min) / buckets;
        
        var histogram = new List<(string range, int count)>();
        
        for (int i = 0; i < buckets; i++)
        {
            var bucketMin = min + i * bucketSize;
            var bucketMax = min + (i + 1) * bucketSize;
            var count = values.Count(v => v >= bucketMin && v < bucketMax);
            
            histogram.Add(($"[{bucketMin:F3}, {bucketMax:F3})", count));
        }

        return histogram;
    }
}

/// <summary>
/// Extension methods to add embedding inspection to the main application
/// </summary>
public static class EmbeddingInspectionExtensions
{
    /// <summary>
    /// Add the embedding inspector to the service collection
    /// </summary>
    public static IServiceCollection AddEmbeddingInspection(this IServiceCollection services)
    {
        services.AddSingleton<EmbeddingInspector>();
        return services;
    }
    
    /// <summary>
    /// Add CLI command to run embedding inspection
    /// </summary>
    public static async Task RunEmbeddingInspectionAsync(IServiceProvider serviceProvider, string[] args)
    {
        if (args.Contains("--inspect-embeddings"))
        {
            var inspector = serviceProvider.GetRequiredService<EmbeddingInspector>();
            
            Console.WriteLine("🚀 Starting Embedding Inspection...");
            Console.WriteLine();
            
            await inspector.DemonstrateEmbeddingsAsync();
            await inspector.AnalyzeCodeChangeImpactAsync();
            await inspector.ExploreVectorDimensionsAsync();
            
            Console.WriteLine("✅ Embedding inspection completed!");
            Environment.Exit(0);
        }
    }
}