#!/usr/bin/env python3
"""
Advanced Embedding Visualization with t-SNE and UMAP

This script provides sophisticated visualization techniques for understanding
1536-dimensional code embeddings by projecting them into 2D/3D space.

Usage:
    python visualize_embeddings.py [--input embeddings.json] [--method tsne|umap|pca]
    
Requirements:
    pip install numpy matplotlib plotly scikit-learn umap-learn pandas seaborn
"""

import json
import numpy as np
import matplotlib.pyplot as plt
import plotly.graph_objects as go
import plotly.express as px
from plotly.subplots import make_subplots
import pandas as pd
import seaborn as sns
from sklearn.manifold import TSNE
from sklearn.decomposition import PCA
from sklearn.preprocessing import StandardScaler
from sklearn.metrics.pairwise import cosine_similarity
import argparse
import os
from typing import Dict, List, Tuple, Any
import warnings
warnings.filterwarnings('ignore')

# Try to import UMAP (optional)
try:
    import umap
    UMAP_AVAILABLE = True
except ImportError:
    print("⚠️  UMAP not available. Install with: pip install umap-learn")
    UMAP_AVAILABLE = False

class EmbeddingVisualizer:
    """
    Comprehensive embedding visualization toolkit
    """
    
    def __init__(self, embeddings_data: Dict[str, List[float]]):
        """Initialize with embeddings dictionary"""
        self.data = embeddings_data
        self.names = list(embeddings_data.keys())
        self.vectors = np.array(list(embeddings_data.values()))
        self.n_samples, self.n_features = self.vectors.shape
        
        print(f"📊 Loaded {self.n_samples} embeddings with {self.n_features} dimensions")
        
        # Create semantic categories based on naming patterns
        self.categories = self.categorize_embeddings()
        self.colors = self.assign_colors()
    
    def categorize_embeddings(self) -> Dict[str, str]:
        """Automatically categorize embeddings based on naming patterns"""
        categories = {}
        
        for name in self.names:
            name_lower = name.lower()
            if any(keyword in name_lower for keyword in ['auth', 'login', 'jwt', 'permission', 'credential']):
                categories[name] = 'Authentication'
            elif any(keyword in name_lower for keyword in ['database', 'db', 'connection', 'query', 'sql']):
                categories[name] = 'Database'
            elif any(keyword in name_lower for keyword in ['http', 'api', 'request', 'client', 'rest']):
                categories[name] = 'HTTP/API'
            elif any(keyword in name_lower for keyword in ['async', 'task', 'await', 'processing']):
                categories[name] = 'Async/Tasks'
            elif any(keyword in name_lower for keyword in ['service', 'business', 'logic']):
                categories[name] = 'Business Logic'
            else:
                categories[name] = 'Other'
        
        return categories
    
    def assign_colors(self) -> Dict[str, str]:
        """Assign colors to categories"""
        color_palette = {
            'Authentication': '#FF6B6B',
            'Database': '#4ECDC4', 
            'HTTP/API': '#45B7D1',
            'Async/Tasks': '#96CEB4',
            'Business Logic': '#FFEAA7',
            'Other': '#DDA0DD'
        }
        
        return {name: color_palette[category] 
                for name, category in self.categories.items()}
    
    def create_tsne_visualization(self, perplexity: int = 5, n_iter: int = 1000) -> Tuple[np.ndarray, float]:
        """
        Create t-SNE 2D projection with optimal parameters for code embeddings
        
        t-SNE is excellent for revealing local clustering patterns
        """
        print(f"🔄 Computing t-SNE projection (perplexity={perplexity}, iterations={n_iter})...")
        
        # Adjust perplexity based on dataset size
        optimal_perplexity = min(perplexity, (self.n_samples - 1) // 3)
        
        tsne = TSNE(
            n_components=2,
            perplexity=optimal_perplexity,
            n_iter=n_iter,
            random_state=42,
            init='pca',
            learning_rate='auto'
        )
        
        # Standardize features for better t-SNE performance
        scaler = StandardScaler()
        vectors_scaled = scaler.fit_transform(self.vectors)
        
        embedding_2d = tsne.fit_transform(vectors_scaled)
        
        # Calculate stress (quality metric)
        stress = tsne.kl_divergence_
        print(f"✅ t-SNE completed. Final stress: {stress:.2f} (lower is better)")
        
        return embedding_2d, stress
    
    def create_umap_visualization(self, n_neighbors: int = 5, min_dist: float = 0.1) -> np.ndarray:
        """
        Create UMAP 2D projection - often better than t-SNE for global structure
        
        UMAP preserves both local and global structure better than t-SNE
        """
        if not UMAP_AVAILABLE:
            raise ImportError("UMAP is not available. Install with: pip install umap-learn")
        
        print(f"🔄 Computing UMAP projection (neighbors={n_neighbors}, min_dist={min_dist})...")
        
        # Adjust parameters based on dataset size
        optimal_neighbors = min(n_neighbors, self.n_samples - 1)
        
        reducer = umap.UMAP(
            n_components=2,
            n_neighbors=optimal_neighbors,
            min_dist=min_dist,
            metric='cosine',  # Cosine distance works well for embeddings
            random_state=42
        )
        
        embedding_2d = reducer.fit_transform(self.vectors)
        print("✅ UMAP completed")
        
        return embedding_2d
    
    def create_pca_visualization(self) -> Tuple[np.ndarray, float]:
        """
        Create PCA 2D projection - shows maximum variance directions
        """
        print("🔄 Computing PCA projection...")
        
        pca = PCA(n_components=2, random_state=42)
        embedding_2d = pca.fit_transform(self.vectors)
        
        explained_variance = pca.explained_variance_ratio_.sum()
        print(f"✅ PCA completed. Explained variance: {explained_variance:.1%}")
        
        return embedding_2d, explained_variance
    
    def create_interactive_scatter(self, embedding_2d: np.ndarray, method: str, 
                                 metric_value: float = None) -> go.Figure:
        """Create interactive Plotly scatter plot"""
        
        # Create hover text with detailed information
        hover_text = []
        for i, name in enumerate(self.names):
            category = self.categories[name]
            vector = self.vectors[i]
            
            hover_info = f"""
<b>{name}</b><br>
Category: {category}<br>
Position: ({embedding_2d[i, 0]:.3f}, {embedding_2d[i, 1]:.3f})<br>
Vector Norm: {np.linalg.norm(vector):.3f}<br>
Vector Mean: {np.mean(vector):.4f}<br>
Vector Std: {np.std(vector):.4f}
""".strip()
            hover_text.append(hover_info)
        
        # Create scatter plot
        fig = go.Figure()
        
        # Add points by category for better legend
        for category in set(self.categories.values()):
            mask = [self.categories[name] == category for name in self.names]
            indices = [i for i, m in enumerate(mask) if m]
            
            if indices:
                fig.add_trace(go.Scatter(
                    x=embedding_2d[indices, 0],
                    y=embedding_2d[indices, 1],
                    mode='markers+text',
                    name=category,
                    text=[self.names[i] for i in indices],
                    textposition='top center',
                    textfont=dict(size=10),
                    hovertext=[hover_text[i] for i in indices],
                    hoverinfo='text',
                    marker=dict(
                        size=12,
                        color=[self.colors[self.names[i]] for i in indices],
                        line=dict(width=2, color='white'),
                        symbol='circle'
                    )
                ))
        
        # Update layout
        title = f"Code Embeddings Visualization: {method.upper()}"
        if metric_value is not None:
            if method == 'tsne':
                title += f" (Stress: {metric_value:.2f})"
            elif method == 'pca':
                title += f" (Explained Variance: {metric_value:.1%})"
        
        fig.update_layout(
            title=title,
            xaxis_title=f"{method.upper()} Component 1",
            yaxis_title=f"{method.upper()} Component 2",
            hovermode='closest',
            width=1000,
            height=700,
            font=dict(size=12),
            legend=dict(
                orientation="v",
                yanchor="top",
                y=1,
                xanchor="left",
                x=1.02
            )
        )
        
        return fig
    
    def create_similarity_heatmap(self) -> go.Figure:
        """Create interactive similarity heatmap"""
        print("🔄 Computing similarity matrix...")
        
        # Compute cosine similarity matrix
        similarity_matrix = cosine_similarity(self.vectors)
        
        # Create custom hover text
        hover_text = []
        for i in range(len(self.names)):
            row = []
            for j in range(len(self.names)):
                text = f"""
<b>{self.names[i]} vs {self.names[j]}</b><br>
Similarity: {similarity_matrix[i, j]:.4f}<br>
Categories: {self.categories[self.names[i]]} vs {self.categories[self.names[j]]}
""".strip()
                row.append(text)
            hover_text.append(row)
        
        fig = go.Figure(data=go.Heatmap(
            z=similarity_matrix,
            x=self.names,
            y=self.names,
            hovertext=hover_text,
            hoverinfo='text',
            colorscale='RdYlBu_r',
            zmin=0,
            zmax=1,
            colorbar=dict(title="Cosine Similarity")
        ))
        
        fig.update_layout(
            title="Code Embedding Similarity Matrix",
            xaxis_title="Code Samples",
            yaxis_title="Code Samples",
            width=800,
            height=800,
            xaxis=dict(tickangle=-45),
            yaxis=dict(tickangle=0)
        )
        
        print("✅ Similarity heatmap created")
        return fig
    
    def create_dimension_analysis(self, top_dimensions: int = 20) -> go.Figure:
        """Analyze and visualize most important dimensions"""
        print("🔄 Analyzing dimension importance...")
        
        # Calculate dimension statistics
        dim_stats = []
        for d in range(self.n_features):
            values = self.vectors[:, d]
            stat = {
                'dimension': d,
                'mean_abs': np.mean(np.abs(values)),
                'std': np.std(values),
                'max_abs': np.max(np.abs(values)),
                'variance': np.var(values)
            }
            dim_stats.append(stat)
        
        # Sort by importance (using max absolute value as proxy)
        dim_stats.sort(key=lambda x: x['max_abs'], reverse=True)
        top_dims = dim_stats[:top_dimensions]
        
        # Create subplot figure
        fig = make_subplots(
            rows=2, cols=2,
            subplot_titles=(
                'Top Dimensions by Max Activation',
                'Dimension Variance Distribution', 
                'Activation Patterns',
                'Category-Dimension Correlation'
            ),
            specs=[[{"secondary_y": False}, {"secondary_y": False}],
                   [{"secondary_y": False}, {"secondary_y": False}]]
        )
        
        # Plot 1: Bar chart of top dimensions
        fig.add_trace(
            go.Bar(
                x=[f"Dim {d['dimension']}" for d in top_dims],
                y=[d['max_abs'] for d in top_dims],
                name='Max Activation',
                marker_color='steelblue'
            ),
            row=1, col=1
        )
        
        # Plot 2: Histogram of dimension variances
        all_variances = [d['variance'] for d in dim_stats]
        fig.add_trace(
            go.Histogram(
                x=all_variances,
                nbinsx=30,
                name='Variance Distribution',
                marker_color='lightcoral'
            ),
            row=1, col=2
        )
        
        # Plot 3: Dimension activation patterns for top 10
        top_10_dims = [d['dimension'] for d in top_dims[:10]]
        activation_data = self.vectors[:, top_10_dims]
        
        fig.add_trace(
            go.Heatmap(
                z=activation_data.T,
                x=self.names,
                y=[f"Dim {d}" for d in top_10_dims],
                colorscale='RdBu',
                zmid=0,
                showscale=False
            ),
            row=2, col=1
        )
        
        # Plot 4: Category analysis (simplified)
        category_means = {}
        for category in set(self.categories.values()):
            indices = [i for i, name in enumerate(self.names) if self.categories[name] == category]
            if indices:
                category_vectors = self.vectors[indices]
                category_means[category] = np.mean(np.abs(category_vectors), axis=0)
        
        # Show mean activation by category for top dimensions
        category_names = list(category_means.keys())
        top_5_dims = top_10_dims[:5]
        
        for i, dim in enumerate(top_5_dims):
            fig.add_trace(
                go.Bar(
                    x=category_names,
                    y=[category_means[cat][dim] for cat in category_names],
                    name=f'Dim {dim}',
                    opacity=0.7
                ),
                row=2, col=2
            )
        
        fig.update_layout(
            title="Embedding Dimension Analysis",
            height=800,
            showlegend=True
        )
        
        print("✅ Dimension analysis completed")
        return fig
    
    def create_3d_visualization(self, method: str = 'tsne') -> go.Figure:
        """Create 3D visualization"""
        print(f"🔄 Computing 3D {method.upper()} projection...")
        
        if method == 'tsne':
            perplexity = min(5, (self.n_samples - 1) // 3)
            tsne_3d = TSNE(n_components=3, perplexity=perplexity, random_state=42)
            embedding_3d = tsne_3d.fit_transform(StandardScaler().fit_transform(self.vectors))
        
        elif method == 'pca':
            pca_3d = PCA(n_components=3, random_state=42)
            embedding_3d = pca_3d.fit_transform(self.vectors)
        
        elif method == 'umap' and UMAP_AVAILABLE:
            umap_3d = umap.UMAP(n_components=3, n_neighbors=min(5, self.n_samples-1), random_state=42)
            embedding_3d = umap_3d.fit_transform(self.vectors)
        
        else:
            raise ValueError(f"Unsupported 3D method: {method}")
        
        # Create 3D scatter plot
        fig = go.Figure()
        
        for category in set(self.categories.values()):
            indices = [i for i, name in enumerate(self.names) if self.categories[name] == category]
            if indices:
                fig.add_trace(go.Scatter3d(
                    x=embedding_3d[indices, 0],
                    y=embedding_3d[indices, 1], 
                    z=embedding_3d[indices, 2],
                    mode='markers+text',
                    name=category,
                    text=[self.names[i] for i in indices],
                    textposition='top center',
                    marker=dict(
                        size=8,
                        color=[self.colors[self.names[i]] for i in indices],
                        line=dict(width=2, color='white')
                    )
                ))
        
        fig.update_layout(
            title=f"3D Code Embeddings Visualization: {method.upper()}",
            scene=dict(
                xaxis_title=f"{method.upper()} Component 1",
                yaxis_title=f"{method.upper()} Component 2",
                zaxis_title=f"{method.upper()} Component 3"
            ),
            width=900,
            height=700
        )
        
        print(f"✅ 3D {method} visualization created")
        return fig
    
    def export_visualizations(self, output_dir: str = "embedding_visualizations"):
        """Export all visualizations to HTML files"""
        os.makedirs(output_dir, exist_ok=True)
        print(f"📁 Creating visualizations in {output_dir}/")
        
        # 2D visualizations
        methods_2d = [('pca', self.create_pca_visualization), ('tsne', self.create_tsne_visualization)]
        
        if UMAP_AVAILABLE:
            methods_2d.append(('umap', self.create_umap_visualization))
        
        for method_name, method_func in methods_2d:
            try:
                if method_name in ['pca', 'tsne']:
                    embedding_2d, metric = method_func()
                    fig = self.create_interactive_scatter(embedding_2d, method_name, metric)
                else:
                    embedding_2d = method_func()
                    fig = self.create_interactive_scatter(embedding_2d, method_name)
                
                output_file = os.path.join(output_dir, f"embeddings_{method_name}_2d.html")
                fig.write_html(output_file)
                print(f"✅ Saved {output_file}")
                
                # Also create 3D version
                if method_name != 'tsne' or self.n_samples <= 50:  # t-SNE 3D can be slow
                    try:
                        fig_3d = self.create_3d_visualization(method_name)
                        output_file_3d = os.path.join(output_dir, f"embeddings_{method_name}_3d.html")
                        fig_3d.write_html(output_file_3d)
                        print(f"✅ Saved {output_file_3d}")
                    except Exception as e:
                        print(f"⚠️  Could not create 3D {method_name}: {e}")
                        
            except Exception as e:
                print(f"⚠️  Could not create {method_name} visualization: {e}")
        
        # Similarity heatmap
        try:
            fig_heatmap = self.create_similarity_heatmap()
            heatmap_file = os.path.join(output_dir, "embeddings_similarity.html")
            fig_heatmap.write_html(heatmap_file)
            print(f"✅ Saved {heatmap_file}")
        except Exception as e:
            print(f"⚠️  Could not create similarity heatmap: {e}")
        
        # Dimension analysis
        try:
            fig_dims = self.create_dimension_analysis()
            dims_file = os.path.join(output_dir, "embeddings_dimensions.html")
            fig_dims.write_html(dims_file)
            print(f"✅ Saved {dims_file}")
        except Exception as e:
            print(f"⚠️  Could not create dimension analysis: {e}")
        
        # Create summary index file
        self.create_index_file(output_dir)
    
    def create_index_file(self, output_dir: str):
        """Create HTML index file linking to all visualizations"""
        index_html = f"""
<!DOCTYPE html>
<html>
<head>
    <title>Code Embedding Visualizations</title>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 40px; line-height: 1.6; }}
        .header {{ background: #f8f9fa; padding: 20px; border-radius: 8px; margin-bottom: 30px; }}
        .viz-grid {{ display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 20px; }}
        .viz-card {{ background: white; padding: 20px; border: 1px solid #dee2e6; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }}
        .viz-card h3 {{ margin-top: 0; color: #495057; }}
        .viz-card a {{ color: #007bff; text-decoration: none; }}
        .viz-card a:hover {{ text-decoration: underline; }}
        .stats {{ background: #e8f4f8; padding: 15px; border-radius: 5px; margin: 20px 0; }}
    </style>
</head>
<body>
    <div class="header">
        <h1>🎯 Code Embedding Visualizations</h1>
        <p>Interactive visualizations of {self.n_samples} code embeddings with {self.n_features} dimensions</p>
    </div>
    
    <div class="stats">
        <h3>📊 Dataset Statistics</h3>
        <ul>
            <li><strong>Samples:</strong> {self.n_samples} code snippets</li>
            <li><strong>Dimensions:</strong> {self.n_features} (OpenAI ada-002)</li>
            <li><strong>Categories:</strong> {', '.join(set(self.categories.values()))}</li>
            <li><strong>Memory:</strong> {self.vectors.nbytes / 1024:.1f} KB</li>
        </ul>
    </div>
    
    <div class="viz-grid">
        <div class="viz-card">
            <h3>🗺️ 2D Projections</h3>
            <p>Reduce 1536D to 2D for visualization</p>
            <ul>
                <li><a href="embeddings_pca_2d.html">PCA 2D</a> - Linear dimensionality reduction</li>
                <li><a href="embeddings_tsne_2d.html">t-SNE 2D</a> - Nonlinear, preserves local structure</li>
                {"<li><a href='embeddings_umap_2d.html'>UMAP 2D</a> - Preserves global structure</li>" if UMAP_AVAILABLE else ""}
            </ul>
        </div>
        
        <div class="viz-card">
            <h3>📦 3D Projections</h3>
            <p>Interactive 3D embedding spaces</p>
            <ul>
                <li><a href="embeddings_pca_3d.html">PCA 3D</a> - 3D linear projection</li>
                {"<li><a href='embeddings_umap_3d.html'>UMAP 3D</a> - 3D nonlinear projection</li>" if UMAP_AVAILABLE else ""}
            </ul>
        </div>
        
        <div class="viz-card">
            <h3>🔥 Similarity Analysis</h3>
            <p>Pairwise similarity between embeddings</p>
            <ul>
                <li><a href="embeddings_similarity.html">Similarity Heatmap</a> - Cosine similarity matrix</li>
            </ul>
        </div>
        
        <div class="viz-card">
            <h3>📊 Dimension Analysis</h3>
            <p>Understanding the 1536 dimensions</p>
            <ul>
                <li><a href="embeddings_dimensions.html">Dimension Importance</a> - Which dimensions matter most</li>
            </ul>
        </div>
    </div>
    
    <div class="stats">
        <h3>💡 How to Use These Visualizations</h3>
        <ul>
            <li><strong>Start with PCA 2D</strong> - Shows the main variation in your embeddings</li>
            <li><strong>Try t-SNE for clusters</strong> - Reveals local similarity patterns</li>
            <li><strong>Use UMAP for balance</strong> - Good compromise between local and global structure</li>
            <li><strong>Check similarity heatmap</strong> - Validate that similar code has high similarity scores</li>
            <li><strong>Explore dimensions</strong> - Understand which aspects of code the model captures</li>
        </ul>
    </div>
</body>
</html>
"""
        
        index_file = os.path.join(output_dir, "index.html")
        with open(index_file, 'w') as f:
            f.write(index_html)
        
        print(f"✅ Saved {index_file}")
        print(f"\n🎉 All visualizations complete! Open {index_file} in your browser")

def create_sample_embeddings() -> Dict[str, List[float]]:
    """Create sample embeddings for demonstration"""
    print("🔧 Creating sample embeddings for demonstration...")
    
    np.random.seed(42)
    
    samples = {
        # Authentication cluster
        "Auth_Login": np.random.normal(0.3, 0.2, 1536),
        "Auth_JWT": np.random.normal(0.25, 0.2, 1536),
        "Auth_Permissions": np.random.normal(0.28, 0.2, 1536),
        
        # Database cluster  
        "Database_Connection": np.random.normal(-0.2, 0.15, 1536),
        "Database_Query": np.random.normal(-0.18, 0.15, 1536),
        
        # HTTP cluster
        "HTTP_Client": np.random.normal(0.1, 0.25, 1536),
        "HTTP_API": np.random.normal(0.12, 0.25, 1536),
        
        # Async cluster
        "Async_Method": np.random.normal(-0.1, 0.3, 1536),
        "Task_Processing": np.random.normal(-0.08, 0.3, 1536),
    }
    
    # Add some correlation between similar categories
    # Make auth samples more similar to each other
    auth_base = np.random.normal(0, 0.1, 1536)
    samples["Auth_Login"][:100] += auth_base[:100]
    samples["Auth_JWT"][:100] += auth_base[:100] * 0.8
    samples["Auth_Permissions"][:100] += auth_base[:100] * 0.7
    
    # Make database samples more similar
    db_base = np.random.normal(0, 0.1, 1536)
    samples["Database_Connection"][100:200] += db_base[:100]
    samples["Database_Query"][100:200] += db_base[:100] * 0.9
    
    # Convert to lists for JSON serialization
    return {name: vector.tolist() for name, vector in samples.items()}

def main():
    """Main function"""
    parser = argparse.ArgumentParser(description="Visualize code embeddings")
    parser.add_argument("--input", help="JSON file with embeddings", default=None)
    parser.add_argument("--method", choices=['all', 'pca', 'tsne', 'umap'], 
                       default='all', help="Visualization method")
    parser.add_argument("--output", default="embedding_visualizations", 
                       help="Output directory")
    parser.add_argument("--sample", action='store_true', 
                       help="Use sample data for demonstration")
    
    args = parser.parse_args()
    
    print("🎯 Code Embedding Visualization Toolkit")
    print("=" * 50)
    
    # Load embeddings
    if args.sample or not args.input:
        print("📊 Using sample embeddings for demonstration")
        embeddings_data = create_sample_embeddings()
    else:
        print(f"📁 Loading embeddings from {args.input}")
        with open(args.input, 'r') as f:
            embeddings_data = json.load(f)
    
    # Create visualizer
    visualizer = EmbeddingVisualizer(embeddings_data)
    
    # Export all visualizations
    visualizer.export_visualizations(args.output)
    
    print(f"\n🎉 Visualization complete!")
    print(f"📂 Results saved to: {args.output}/")
    print(f"🌐 Open {args.output}/index.html to explore your embeddings")

if __name__ == "__main__":
    main()