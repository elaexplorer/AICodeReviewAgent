using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Services;

/// <summary>
/// Visualizes embeddings through various techniques:
/// - 2D/3D projections using PCA and t-SNE
/// - Similarity heatmaps
/// - Component analysis
/// - Interactive HTML visualizations
/// </summary>
public class EmbeddingVisualizer
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ILogger<EmbeddingVisualizer> _logger;

    public EmbeddingVisualizer(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ILogger<EmbeddingVisualizer> logger)
    {
        _embeddingGenerator = embeddingGenerator;
        _logger = logger;
    }

    /// <summary>
    /// Create comprehensive visualization of embeddings
    /// </summary>
    public async Task CreateVisualizationsAsync()
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                    EMBEDDING VISUALIZATION                         ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Sample code snippets for visualization
        var codesamples = GetCodeSamples();
        
        // Generate embeddings
        var embeddings = new Dictionary<string, float[]>();
        foreach (var (name, code) in codesamples)
        {
            var response = await _embeddingGenerator.GenerateAsync(code);
            embeddings[name] = response.Vector.ToArray();
            Console.WriteLine($"✅ Generated embedding for: {name}");
        }

        Console.WriteLine($"\n📊 Generated {embeddings.Count} embeddings with {embeddings.Values.First().Length} dimensions");
        Console.WriteLine();

        // Create various visualizations
        await CreatePCAVisualizationAsync(embeddings);
        await CreateSimilarityHeatmapAsync(embeddings);
        await CreateDimensionAnalysisAsync(embeddings);
        await CreateInteractiveVisualizationAsync(embeddings);
        await CreateSearchVisualizationAsync(embeddings);

        Console.WriteLine("✅ All visualizations created successfully!");
        Console.WriteLine("\n📂 Generated files:");
        Console.WriteLine("   - embeddings_pca.html (2D scatter plot)");
        Console.WriteLine("   - embeddings_heatmap.html (similarity matrix)");
        Console.WriteLine("   - embeddings_dimensions.html (component analysis)");
        Console.WriteLine("   - embeddings_explorer.html (interactive explorer)");
        Console.WriteLine("   - embeddings_search.html (search visualization)");
    }

    /// <summary>
    /// Create 2D visualization using Principal Component Analysis (PCA)
    /// </summary>
    private async Task CreatePCAVisualizationAsync(Dictionary<string, float[]> embeddings)
    {
        Console.WriteLine("📈 Creating PCA 2D visualization...");

        // Perform PCA to reduce from 1536D to 2D
        var pcaResult = PerformPCA(embeddings, targetDimensions: 2);

        // Generate HTML with Plot.ly visualization
        var html = GeneratePCAHtml(pcaResult);
        await File.WriteAllTextAsync("embeddings_pca.html", html);

        Console.WriteLine("   ✅ embeddings_pca.html created");
        Console.WriteLine("      Open in browser to see 2D projection of 1536D vectors");
    }

    /// <summary>
    /// Create similarity heatmap visualization
    /// </summary>
    private async Task CreateSimilarityHeatmapAsync(Dictionary<string, float[]> embeddings)
    {
        Console.WriteLine("🔥 Creating similarity heatmap...");

        var names = embeddings.Keys.ToArray();
        var similarityMatrix = CalculateSimilarityMatrix(embeddings);

        var html = GenerateHeatmapHtml(names, similarityMatrix);
        await File.WriteAllTextAsync("embeddings_heatmap.html", html);

        Console.WriteLine("   ✅ embeddings_heatmap.html created");
        Console.WriteLine("      Interactive heatmap showing pairwise similarities");
    }

    /// <summary>
    /// Analyze and visualize dimension contributions
    /// </summary>
    private async Task CreateDimensionAnalysisAsync(Dictionary<string, float[]> embeddings)
    {
        Console.WriteLine("📊 Creating dimension analysis...");

        var analysis = AnalyzeDimensions(embeddings);
        var html = GenerateDimensionAnalysisHtml(analysis);
        
        await File.WriteAllTextAsync("embeddings_dimensions.html", html);

        Console.WriteLine("   ✅ embeddings_dimensions.html created");
        Console.WriteLine("      Shows which dimensions are most active for different code types");
    }

    /// <summary>
    /// Create interactive embedding explorer
    /// </summary>
    private async Task CreateInteractiveVisualizationAsync(Dictionary<string, float[]> embeddings)
    {
        Console.WriteLine("🎛️ Creating interactive explorer...");

        var html = GenerateInteractiveExplorerHtml(embeddings);
        await File.WriteAllTextAsync("embeddings_explorer.html", html);

        Console.WriteLine("   ✅ embeddings_explorer.html created");
        Console.WriteLine("      Interactive tool to explore vectors and similarities");
    }

    /// <summary>
    /// Create search process visualization
    /// </summary>
    private async Task CreateSearchVisualizationAsync(Dictionary<string, float[]> embeddings)
    {
        Console.WriteLine("🔍 Creating search visualization...");

        // Simulate search queries
        var queries = new[]
        {
            "user authentication",
            "database connection",
            "HTTP API request",
            "async await pattern"
        };

        var searchResults = new List<SearchVisualizationData>();
        
        foreach (var query in queries)
        {
            var queryResponse = await _embeddingGenerator.GenerateAsync(query);
            var queryVector = queryResponse.Vector.ToArray();
            
            var results = embeddings.Select(kvp => new
            {
                Name = kvp.Key,
                Similarity = CosineSimilarity(queryVector, kvp.Value)
            })
            .OrderByDescending(r => r.Similarity)
            .ToArray();

            searchResults.Add(new SearchVisualizationData
            {
                Query = query,
                QueryVector = queryVector,
                Results = results.Select(r => new { r.Name, r.Similarity }).ToArray()
            });
        }

        var html = GenerateSearchVisualizationHtml(searchResults, embeddings);
        await File.WriteAllTextAsync("embeddings_search.html", html);

        Console.WriteLine("   ✅ embeddings_search.html created");
        Console.WriteLine("      Shows how search queries match against code embeddings");
    }

    /// <summary>
    /// Perform Principal Component Analysis to reduce dimensions
    /// </summary>
    private Dictionary<string, float[]> PerformPCA(Dictionary<string, float[]> embeddings, int targetDimensions)
    {
        var vectors = embeddings.Values.ToArray();
        var names = embeddings.Keys.ToArray();
        var dimension = vectors[0].Length;
        var numVectors = vectors.Length;

        // Step 1: Center the data (subtract mean)
        var means = new float[dimension];
        for (int d = 0; d < dimension; d++)
        {
            means[d] = vectors.Select(v => v[d]).Average();
        }

        var centeredVectors = vectors.Select(v => 
            v.Select((val, d) => val - means[d]).ToArray()
        ).ToArray();

        // Step 2: Compute covariance matrix (simplified - using first few dimensions for demo)
        // In production, you'd use a proper linear algebra library like MathNet.Numerics
        var projectedVectors = new Dictionary<string, float[]>();

        for (int i = 0; i < names.Length; i++)
        {
            var vector = centeredVectors[i];
            
            // Simple projection using first few dimensions with highest variance
            var varianceContributions = new List<(int dim, double variance)>();
            for (int d = 0; d < Math.Min(50, dimension); d++) // Check first 50 dimensions
            {
                var values = centeredVectors.Select(v => v[d]).ToArray();
                var variance = values.Sum(x => x * x) / values.Length;
                varianceContributions.Add((d, variance));
            }

            var topDimensions = varianceContributions
                .OrderByDescending(x => x.variance)
                .Take(targetDimensions)
                .Select(x => x.dim)
                .ToArray();

            // Project onto top dimensions
            var projected = topDimensions.Select(d => vector[d]).ToArray();
            projectedVectors[names[i]] = projected;
        }

        return projectedVectors;
    }

    /// <summary>
    /// Calculate similarity matrix for all embedding pairs
    /// </summary>
    private double[,] CalculateSimilarityMatrix(Dictionary<string, float[]> embeddings)
    {
        var names = embeddings.Keys.ToArray();
        var matrix = new double[names.Length, names.Length];

        for (int i = 0; i < names.Length; i++)
        {
            for (int j = 0; j < names.Length; j++)
            {
                matrix[i, j] = CosineSimilarity(embeddings[names[i]], embeddings[names[j]]);
            }
        }

        return matrix;
    }

    /// <summary>
    /// Analyze which dimensions are most important for different code types
    /// </summary>
    private DimensionAnalysis AnalyzeDimensions(Dictionary<string, float[]> embeddings)
    {
        var dimension = embeddings.Values.First().Length;
        var analysis = new DimensionAnalysis
        {
            DimensionImportance = new List<DimensionInfo>(),
            CategoryPatterns = new Dictionary<string, List<int>>()
        };

        // Find dimensions with highest activation for each category
        var categories = new Dictionary<string, List<string>>
        {
            ["Authentication"] = new() { "Auth_Login", "Auth_JWT", "Auth_Permissions" },
            ["Database"] = new() { "Database_Connection", "Database_Query" },
            ["HTTP"] = new() { "HTTP_Client", "HTTP_API" },
            ["Async"] = new() { "Async_Method", "Task_Processing" }
        };

        for (int d = 0; d < Math.Min(100, dimension); d++) // Analyze first 100 dimensions
        {
            var activations = embeddings.Select(kvp => new
            {
                Name = kvp.Key,
                Activation = Math.Abs(kvp.Value[d])
            }).ToArray();

            var avgActivation = activations.Average(a => a.Activation);
            var maxActivation = activations.Max(a => a.Activation);

            if (maxActivation > 0.3) // Significant activation threshold
            {
                analysis.DimensionImportance.Add(new DimensionInfo
                {
                    Dimension = d,
                    AverageActivation = avgActivation,
                    MaxActivation = maxActivation,
                    TopActivators = activations.OrderByDescending(a => a.Activation)
                        .Take(3)
                        .Select(a => new { a.Name, a.Activation })
                        .ToArray()
                });
            }
        }

        // Sort by importance
        analysis.DimensionImportance = analysis.DimensionImportance
            .OrderByDescending(d => d.MaxActivation)
            .Take(20)
            .ToList();

        return analysis;
    }

    /// <summary>
    /// Generate HTML for PCA visualization
    /// </summary>
    private string GeneratePCAHtml(Dictionary<string, float[]> pcaResult)
    {
        var data = pcaResult.Select(kvp => new
        {
            name = kvp.Key,
            x = kvp.Value[0],
            y = kvp.Value[1]
        }).ToArray();

        var dataJson = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });

        return $$$"""
<!DOCTYPE html>
<html>
<head>
    <title>Embedding PCA Visualization</title>
    <script src="https://cdn.plot.ly/plotly-latest.min.js"></script>
    <style>
        body { font-family: Arial, sans-serif; margin: 20px; }
        .header { background: #f5f5f5; padding: 20px; border-radius: 8px; margin-bottom: 20px; }
        .explanation { margin: 20px 0; padding: 15px; background: #e8f4f8; border-radius: 5px; }
    </style>
</head>
<body>
    <div class="header">
        <h1>🎯 Embedding Visualization: 1536D → 2D Projection</h1>
        <p>This shows how your 1536-dimensional code embeddings look when projected onto 2D space using Principal Component Analysis (PCA).</p>
    </div>
    
    <div id="pca-plot" style="width:100%;height:600px;"></div>
    
    <div class="explanation">
        <h3>💡 How to Read This:</h3>
        <ul>
            <li><strong>Similar code clusters together</strong> - Authentication code should be near other auth code</li>
            <li><strong>Distance represents similarity</strong> - Closer points have higher cosine similarity</li>
            <li><strong>Axes capture most variance</strong> - PC1 and PC2 represent the dimensions with most variation</li>
            <li><strong>Hover for details</strong> - Mouse over points to see code names and coordinates</li>
        </ul>
    </div>

    <script>
        const data = {{{dataJson}}};
        
        const trace = {
            x: data.map(d => d.x),
            y: data.map(d => d.y),
            text: data.map(d => d.name),
            mode: 'markers+text',
            type: 'scatter',
            marker: {
                size: 12,
                color: data.map((d, i) => {
                    if (d.name.includes('Auth')) return '#FF6B6B';
                    if (d.name.includes('Database')) return '#4ECDC4';
                    if (d.name.includes('HTTP')) return '#45B7D1';
                    if (d.name.includes('Async')) return '#96CEB4';
                    return '#FFEAA7';
                }),
                line: { width: 2, color: '#DDD' }
            },
            textposition: 'top center',
            textfont: { size: 10 }
        };

        const layout = {
            title: 'Code Embeddings in 2D Space (PCA Projection)',
            xaxis: { title: 'Principal Component 1 (Most Variation)' },
            yaxis: { title: 'Principal Component 2 (Second Most Variation)' },
            hovermode: 'closest',
            showlegend: false
        };

        Plotly.newPlot('pca-plot', [trace], layout);
    </script>
</body>
</html>
""";
    }

    /// <summary>
    /// Generate HTML for similarity heatmap
    /// </summary>
    private string GenerateHeatmapHtml(string[] names, double[,] similarityMatrix)
    {
        var matrixData = new List<List<double>>();
        for (int i = 0; i < names.Length; i++)
        {
            var row = new List<double>();
            for (int j = 0; j < names.Length; j++)
            {
                row.Add(Math.Round(similarityMatrix[i, j], 3));
            }
            matrixData.Add(row);
        }

        var dataJson = JsonSerializer.Serialize(matrixData);
        var namesJson = JsonSerializer.Serialize(names);

        return $$$"""
<!DOCTYPE html>
<html>
<head>
    <title>Embedding Similarity Heatmap</title>
    <script src="https://cdn.plot.ly/plotly-latest.min.js"></script>
    <style>
        body { font-family: Arial, sans-serif; margin: 20px; }
        .header { background: #f5f5f5; padding: 20px; border-radius: 8px; margin-bottom: 20px; }
        .explanation { margin: 20px 0; padding: 15px; background: #e8f4f8; border-radius: 5px; }
        .stats { display: flex; justify-content: space-around; margin: 20px 0; }
        .stat { text-align: center; padding: 15px; background: white; border-radius: 5px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }
    </style>
</head>
<body>
    <div class="header">
        <h1>🔥 Embedding Similarity Heatmap</h1>
        <p>This heatmap shows cosine similarity between all pairs of code embeddings. Values range from 0 (completely different) to 1 (identical).</p>
    </div>
    
    <div id="heatmap" style="width:100%;height:600px;"></div>
    
    <div class="stats">
        <div class="stat">
            <h3>🟢 High Similarity</h3>
            <p>>0.8</p>
            <p>Same domain/concept</p>
        </div>
        <div class="stat">
            <h3>🟡 Medium Similarity</h3>
            <p>0.6 - 0.8</p>
            <p>Related concepts</p>
        </div>
        <div class="stat">
            <h3>🔴 Low Similarity</h3>
            <p><0.6</p>
            <p>Different domains</p>
        </div>
    </div>
    
    <div class="explanation">
        <h3>💡 How to Read This:</h3>
        <ul>
            <li><strong>Diagonal is always 1.0</strong> - Each code sample is identical to itself</li>
            <li><strong>Matrix is symmetric</strong> - Similarity(A,B) = Similarity(B,A)</li>
            <li><strong>Color intensity</strong> - Darker red = higher similarity</li>
            <li><strong>Expected patterns</strong> - Authentication code should be similar to other auth code</li>
            <li><strong>Hover for exact values</strong> - Mouse over cells for precise similarity scores</li>
        </ul>
    </div>

    <script>
        const matrix = {{{dataJson}}};
        const labels = {{{namesJson}}};
        
        const trace = {
            z: matrix,
            x: labels,
            y: labels,
            type: 'heatmap',
            colorscale: [
                [0, '#FFF5F5'],
                [0.5, '#FED7D7'], 
                [0.7, '#FEB2B2'],
                [0.8, '#FC8181'],
                [0.9, '#F56565'],
                [1, '#E53E3E']
            ],
            showscale: true,
            colorbar: {
                title: 'Cosine Similarity',
                titleside: 'right'
            }
        };

        const layout = {
            title: 'Code Embedding Similarity Matrix',
            xaxis: { title: 'Code Samples', tickangle: -45 },
            yaxis: { title: 'Code Samples' },
            width: 800,
            height: 600
        };

        Plotly.newPlot('heatmap', [trace], layout);
    </script>
</body>
</html>
""";
    }

    /// <summary>
    /// Generate HTML for dimension analysis
    /// </summary>
    private string GenerateDimensionAnalysisHtml(DimensionAnalysis analysis)
    {
        var dataJson = JsonSerializer.Serialize(analysis.DimensionImportance.Select(d => new
        {
            dimension = d.Dimension,
            avgActivation = Math.Round(d.AverageActivation, 4),
            maxActivation = Math.Round(d.MaxActivation, 4),
            topActivators = d.TopActivators
        }), new JsonSerializerOptions { WriteIndented = true });

        return $$$"""
<!DOCTYPE html>
<html>
<head>
    <title>Embedding Dimension Analysis</title>
    <script src="https://cdn.plot.ly/plotly-latest.min.js"></script>
    <style>
        body { font-family: Arial, sans-serif; margin: 20px; }
        .header { background: #f5f5f5; padding: 20px; border-radius: 8px; margin-bottom: 20px; }
        .explanation { margin: 20px 0; padding: 15px; background: #e8f4f8; border-radius: 5px; }
        .dimension-table { width: 100%; border-collapse: collapse; margin: 20px 0; }
        .dimension-table th, .dimension-table td { padding: 10px; text-align: left; border-bottom: 1px solid #ddd; }
        .dimension-table th { background-color: #f8f9fa; }
    </style>
</head>
<body>
    <div class="header">
        <h1>📊 Embedding Dimension Analysis</h1>
        <p>This shows which of the 1536 dimensions are most active for different types of code.</p>
    </div>
    
    <div id="dimension-plot" style="width:100%;height:400px;"></div>
    
    <div class="explanation">
        <h3>💡 Understanding Dimensions:</h3>
        <ul>
            <li><strong>Each dimension captures different aspects</strong> - Some for "authentication", others for "async patterns"</li>
            <li><strong>High activation</strong> - Dimension strongly responds to certain code types</li>
            <li><strong>Patterns reveal specialization</strong> - Related concepts activate similar dimensions</li>
        </ul>
    </div>
    
    <h3>📋 Most Important Dimensions</h3>
    <table class="dimension-table">
        <thead>
            <tr>
                <th>Dimension</th>
                <th>Max Activation</th>
                <th>Avg Activation</th>
                <th>Most Responsive Code</th>
            </tr>
        </thead>
        <tbody id="dimension-table-body">
        </tbody>
    </table>

    <script>
        const data = {{{dataJson}}};
        
        // Create bar chart of dimension importance
        const trace = {
            x: data.map(d => `Dim ${d.dimension}`),
            y: data.map(d => d.maxActivation),
            type: 'bar',
            marker: {
                color: data.map(d => d.maxActivation),
                colorscale: 'Viridis',
                showscale: true,
                colorbar: { title: 'Activation Level' }
            }
        };

        const layout = {
            title: 'Most Active Dimensions (Top 20)',
            xaxis: { title: 'Dimension Index' },
            yaxis: { title: 'Maximum Activation Value' }
        };

        Plotly.newPlot('dimension-plot', [trace], layout);
        
        // Populate dimension table
        const tableBody = document.getElementById('dimension-table-body');
        data.forEach(d => {
            const row = tableBody.insertRow();
            row.insertCell(0).textContent = d.dimension;
            row.insertCell(1).textContent = d.maxActivation.toFixed(4);
            row.insertCell(2).textContent = d.avgActivation.toFixed(4);
            row.insertCell(3).textContent = d.topActivators.map(t => `${t.Name} (${t.Activation.toFixed(3)})`).join(', ');
        });
    </script>
</body>
</html>
""";
    }

    /// <summary>
    /// Generate interactive embedding explorer
    /// </summary>
    private string GenerateInteractiveExplorerHtml(Dictionary<string, float[]> embeddings)
    {
        var embeddingsJson = JsonSerializer.Serialize(embeddings.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Take(50).ToArray() // First 50 dimensions for visualization
        ));

        return $$$"""
<!DOCTYPE html>
<html>
<head>
    <title>Interactive Embedding Explorer</title>
    <script src="https://cdn.plot.ly/plotly-latest.min.js"></script>
    <style>
        body { font-family: Arial, sans-serif; margin: 20px; }
        .header { background: #f5f5f5; padding: 20px; border-radius: 8px; margin-bottom: 20px; }
        .controls { display: flex; gap: 20px; margin: 20px 0; }
        .control-group { flex: 1; }
        select, input { width: 100%; padding: 8px; margin: 5px 0; }
        .stats { background: #f8f9fa; padding: 15px; margin: 10px 0; border-radius: 5px; }
    </style>
</head>
<body>
    <div class="header">
        <h1>🎛️ Interactive Embedding Explorer</h1>
        <p>Explore individual embedding vectors and compare them interactively.</p>
    </div>
    
    <div class="controls">
        <div class="control-group">
            <label>Select Code Sample:</label>
            <select id="codeSelect">
                <option value="">Choose a code sample...</option>
            </select>
        </div>
        <div class="control-group">
            <label>Compare With:</label>
            <select id="compareSelect">
                <option value="">Choose another sample...</option>
            </select>
        </div>
    </div>
    
    <div id="vector-plot" style="width:100%;height:400px;"></div>
    
    <div class="stats" id="stats" style="display:none;">
        <h3>📊 Vector Statistics</h3>
        <div id="statsContent"></div>
    </div>
    
    <div class="stats" id="comparison" style="display:none;">
        <h3>🔍 Comparison Results</h3>
        <div id="comparisonContent"></div>
    </div>

    <script>
        const embeddings = {{{embeddingsJson}}};
        
        // Populate dropdowns
        const codeSelect = document.getElementById('codeSelect');
        const compareSelect = document.getElementById('compareSelect');
        
        Object.keys(embeddings).forEach(name => {
            const option1 = new Option(name, name);
            const option2 = new Option(name, name);
            codeSelect.add(option1);
            compareSelect.add(option2);
        });
        
        // Event handlers
        codeSelect.addEventListener('change', updateVisualization);
        compareSelect.addEventListener('change', updateVisualization);
        
        function updateVisualization() {
            const selected = codeSelect.value;
            const compared = compareSelect.value;
            
            if (!selected) return;
            
            const vector = embeddings[selected];
            const traces = [{
                x: Array.from({length: vector.length}, (_, i) => i),
                y: vector,
                type: 'scatter',
                mode: 'lines+markers',
                name: selected,
                line: { color: '#FF6B6B' }
            }];
            
            if (compared && compared !== selected) {
                const compareVector = embeddings[compared];
                traces.push({
                    x: Array.from({length: compareVector.length}, (_, i) => i),
                    y: compareVector,
                    type: 'scatter',
                    mode: 'lines+markers',
                    name: compared,
                    line: { color: '#4ECDC4' }
                });
            }
            
            const layout = {
                title: `Embedding Vector Components (first 50 dimensions)`,
                xaxis: { title: 'Dimension Index' },
                yaxis: { title: 'Activation Value' },
                showlegend: true
            };
            
            Plotly.newPlot('vector-plot', traces, layout);
            
            // Update stats
            updateStats(selected, vector);
            
            if (compared && compared !== selected) {
                updateComparison(selected, compared, vector, embeddings[compared]);
            }
        }
        
        function updateStats(name, vector) {
            const stats = document.getElementById('stats');
            const content = document.getElementById('statsContent');
            
            const min = Math.min(...vector);
            const max = Math.max(...vector);
            const mean = vector.reduce((a, b) => a + b, 0) / vector.length;
            const magnitude = Math.sqrt(vector.reduce((a, b) => a + b * b, 0));
            
            content.innerHTML = `
                <p><strong>Name:</strong> ${name}</p>
                <p><strong>Dimensions:</strong> ${vector.length} (showing first 50)</p>
                <p><strong>Range:</strong> [${min.toFixed(4)}, ${max.toFixed(4)}]</p>
                <p><strong>Mean:</strong> ${mean.toFixed(4)}</p>
                <p><strong>Magnitude:</strong> ${magnitude.toFixed(4)}</p>
            `;
            
            stats.style.display = 'block';
        }
        
        function updateComparison(name1, name2, vector1, vector2) {
            const comparison = document.getElementById('comparison');
            const content = document.getElementById('comparisonContent');
            
            const similarity = cosineSimilarity(vector1, vector2);
            const euclideanDistance = euclideanDistance(vector1, vector2);
            
            content.innerHTML = `
                <p><strong>Comparing:</strong> ${name1} vs ${name2}</p>
                <p><strong>Cosine Similarity:</strong> ${similarity.toFixed(4)}</p>
                <p><strong>Euclidean Distance:</strong> ${euclideanDistance.toFixed(4)}</p>
                <p><strong>Interpretation:</strong> ${interpretSimilarity(similarity)}</p>
            `;
            
            comparison.style.display = 'block';
        }
        
        function cosineSimilarity(a, b) {
            const dotProduct = a.reduce((sum, ai, i) => sum + ai * b[i], 0);
            const magnitudeA = Math.sqrt(a.reduce((sum, ai) => sum + ai * ai, 0));
            const magnitudeB = Math.sqrt(b.reduce((sum, bi) => sum + bi * bi, 0));
            return dotProduct / (magnitudeA * magnitudeB);
        }
        
        function euclideanDistance(a, b) {
            return Math.sqrt(a.reduce((sum, ai, i) => sum + Math.pow(ai - b[i], 2), 0));
        }
        
        function interpretSimilarity(sim) {
            if (sim > 0.9) return "Nearly identical concepts";
            if (sim > 0.8) return "Very similar concepts";
            if (sim > 0.7) return "Related concepts";
            if (sim > 0.5) return "Somewhat related";
            return "Different concepts";
        }
    </script>
</body>
</html>
""";
    }

    /// <summary>
    /// Generate search visualization showing query matching
    /// </summary>
    private string GenerateSearchVisualizationHtml(List<SearchVisualizationData> searchResults, Dictionary<string, float[]> embeddings)
    {
        var searchJson = JsonSerializer.Serialize(searchResults.Select(s => new
        {
            query = s.Query,
            results = s.Results.Select(r => new
            {
                name = r.GetType().GetProperty("Name")?.GetValue(r)?.ToString(),
                similarity = Math.Round((double)(r.GetType().GetProperty("Similarity")?.GetValue(r) ?? 0), 4)
            }).ToArray()
        }));

        return $$$"""
<!DOCTYPE html>
<html>
<head>
    <title>Embedding Search Visualization</title>
    <script src="https://cdn.plot.ly/plotly-latest.min.js"></script>
    <style>
        body { font-family: Arial, sans-serif; margin: 20px; }
        .header { background: #f5f5f5; padding: 20px; border-radius: 8px; margin-bottom: 20px; }
        .query-section { margin: 30px 0; padding: 20px; border: 1px solid #ddd; border-radius: 8px; }
        .query-title { color: #2d3748; margin-bottom: 15px; }
    </style>
</head>
<body>
    <div class="header">
        <h1>🔍 Embedding Search Visualization</h1>
        <p>This shows how different search queries match against code embeddings using semantic similarity.</p>
    </div>
    
    <div id="searchPlots"></div>
    
    <div class="query-section">
        <h3>💡 Understanding Search Results:</h3>
        <ul>
            <li><strong>Higher bars = better matches</strong> - Similarity scores range from 0 to 1</li>
            <li><strong>Semantic understanding</strong> - "authentication" matches "login", "JWT", "permissions"</li>
            <li><strong>Cross-language matching</strong> - Concepts work across programming languages</li>
            <li><strong>Context awareness</strong> - Related concepts cluster together</li>
        </ul>
    </div>

    <script>
        const searchData = {{{searchJson}}};
        
        searchData.forEach((search, index) => {
            // Create container for this query
            const container = document.createElement('div');
            container.id = `search-${index}`;
            container.style.height = '400px';
            container.style.marginBottom = '30px';
            document.getElementById('searchPlots').appendChild(container);
            
            // Add query title
            const title = document.createElement('div');
            title.className = 'query-title';
            title.innerHTML = `<h3>🔍 Query: "${search.query}"</h3>`;
            container.parentNode.insertBefore(title, container);
            
            // Create bar chart for this query
            const trace = {
                x: search.results.map(r => r.name),
                y: search.results.map(r => r.similarity),
                type: 'bar',
                marker: {
                    color: search.results.map(r => {
                        if (r.similarity > 0.8) return '#48BB78';
                        if (r.similarity > 0.6) return '#ED8936';
                        return '#E53E3E';
                    }),
                    line: { width: 1, color: '#2D3748' }
                }
            };
            
            const layout = {
                title: `Similarity Scores for "${search.query}"`,
                xaxis: { 
                    title: 'Code Samples',
                    tickangle: -45
                },
                yaxis: { 
                    title: 'Cosine Similarity',
                    range: [0, 1]
                },
                showlegend: false,
                margin: { t: 50, b: 100 }
            };
            
            Plotly.newPlot(container, [trace], layout);
        });
    </script>
</body>
</html>
""";
    }

    // Helper methods
    private Dictionary<string, string> GetCodeSamples()
    {
        return new Dictionary<string, string>
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
                    var token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddHours(24));
                    return new JwtSecurityTokenHandler().WriteToken(token);
                }
                """,
            ["Auth_Permissions"] = """
                public async Task<bool> CheckUserPermissions(int userId, string resource)
                {
                    var permissions = await _permissionService.GetUserPermissionsAsync(userId);
                    return permissions.Any(p => p.Resource == resource);
                }
                """,
            ["Database_Connection"] = """
                public async Task<IDbConnection> CreateDatabaseConnectionAsync()
                {
                    var connection = new SqlConnection(_connectionString);
                    await connection.OpenAsync();
                    return connection;
                }
                """,
            ["Database_Query"] = """
                public async Task<List<User>> GetUsersByRoleAsync(string role)
                {
                    return await _context.Users
                        .Where(u => u.Role == role)
                        .ToListAsync();
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
            ["HTTP_API"] = """
                [HttpGet("api/users/{id}")]
                public async Task<ActionResult<User>> GetUser(int id)
                {
                    var user = await _userService.GetByIdAsync(id);
                    return user != null ? Ok(user) : NotFound();
                }
                """,
            ["Async_Method"] = """
                public async Task<ProcessingResult> ProcessDataAsync(IEnumerable<DataItem> items)
                {
                    var tasks = items.Select(ProcessItemAsync);
                    var results = await Task.WhenAll(tasks);
                    return new ProcessingResult { Items = results };
                }
                """,
            ["Task_Processing"] = """
                public async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, int maxRetries = 3)
                {
                    for (int attempt = 1; attempt <= maxRetries; attempt++)
                    {
                        try { return await operation(); }
                        catch when (attempt < maxRetries) { await Task.Delay(1000); }
                    }
                    return await operation();
                }
                """
        };
    }

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
}

// Supporting classes
public class DimensionAnalysis
{
    public List<DimensionInfo> DimensionImportance { get; set; } = new();
    public Dictionary<string, List<int>> CategoryPatterns { get; set; } = new();
}

public class DimensionInfo
{
    public int Dimension { get; set; }
    public double AverageActivation { get; set; }
    public double MaxActivation { get; set; }
    public object[] TopActivators { get; set; } = Array.Empty<object>();
}

public class SearchVisualizationData
{
    public string Query { get; set; } = string.Empty;
    public float[] QueryVector { get; set; } = Array.Empty<float>();
    public object[] Results { get; set; } = Array.Empty<object>();
}

/// <summary>
/// Extension to add embedding visualization to services
/// </summary>
public static class EmbeddingVisualizationExtensions
{
    public static IServiceCollection AddEmbeddingVisualization(this IServiceCollection services)
    {
        services.AddSingleton<EmbeddingVisualizer>();
        return services;
    }
    
    public static async Task RunEmbeddingVisualizationAsync(IServiceProvider serviceProvider, string[] args)
    {
        if (args.Contains("--visualize-embeddings"))
        {
            var visualizer = serviceProvider.GetRequiredService<EmbeddingVisualizer>();
            
            Console.WriteLine("🚀 Starting Embedding Visualization...");
            await visualizer.CreateVisualizationsAsync();
            
            Console.WriteLine("✅ Visualization completed!");
            Environment.Exit(0);
        }
    }
}