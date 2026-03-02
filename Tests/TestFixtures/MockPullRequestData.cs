using CodeReviewAgent.Models;

namespace CodeReviewAgent.Tests.TestFixtures;

public static class MockPullRequestData
{
    public static PullRequestInfo GetSamplePullRequest()
    {
        return new PullRequestInfo
        {
            Id = 12345,
            Title = "Add new feature for user authentication",
            Description = "This PR adds JWT authentication support with role-based access control.",
            Author = "testuser@company.com",
            Status = "Active",
            CreatedDate = DateTime.UtcNow.AddDays(-1),
            SourceBranch = "feature/jwt-auth",
            TargetBranch = "main",
            Repository = "test-repository",
            Project = "TestProject"
        };
    }

    public static List<PullRequestFile> GetSampleChangedFiles()
    {
        return new List<PullRequestFile>
        {
            new PullRequestFile
            {
                Path = "/src/Authentication/JwtService.cs",
                ChangeType = "add",
                Content = GetSampleCSharpFile(),
                UnifiedDiff = GetSampleCSharpDiff(),
                PreviousContent = ""
            },
            new PullRequestFile
            {
                Path = "/src/Controllers/AuthController.cs",
                ChangeType = "edit",
                Content = GetSampleControllerFile(),
                UnifiedDiff = GetSampleControllerDiff(),
                PreviousContent = GetSampleControllerPrevious()
            },
            new PullRequestFile
            {
                Path = "/tests/AuthenticationTests.py",
                ChangeType = "add",
                Content = GetSamplePythonTest(),
                UnifiedDiff = GetSamplePythonDiff(),
                PreviousContent = ""
            }
        };
    }

    private static string GetSampleCSharpFile()
    {
        return @"using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;

namespace Authentication
{
    public class JwtService
    {
        private readonly string _secretKey;
        
        public JwtService(string secretKey)
        {
            _secretKey = secretKey ?? throw new ArgumentNullException(nameof(secretKey));
        }
        
        public string GenerateToken(string userId, string[] roles)
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.ASCII.GetBytes(_secretKey);
            
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.Name, userId),
                    new Claim(ClaimTypes.Role, string.Join("","", roles))
                }),
                Expires = DateTime.UtcNow.AddHours(24),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), 
                    SecurityAlgorithms.HmacSha256Signature)
            };
            
            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }
    }
}";
    }

    private static string GetSampleCSharpDiff()
    {
        return @"@@ -0,0 +1,33 @@
+using System.IdentityModel.Tokens.Jwt;
+using Microsoft.IdentityModel.Tokens;
+
+namespace Authentication
+{
+    public class JwtService
+    {
+        private readonly string _secretKey;
+        
+        public JwtService(string secretKey)
+        {
+            _secretKey = secretKey ?? throw new ArgumentNullException(nameof(secretKey));
+        }
+        
+        public string GenerateToken(string userId, string[] roles)
+        {
+            var tokenHandler = new JwtSecurityTokenHandler();
+            var key = Encoding.ASCII.GetBytes(_secretKey);
+            
+            var tokenDescriptor = new SecurityTokenDescriptor
+            {
+                Subject = new ClaimsIdentity(new[]
+                {
+                    new Claim(ClaimTypes.Name, userId),
+                    new Claim(ClaimTypes.Role, string.Join("","", roles))
+                }),
+                Expires = DateTime.UtcNow.AddHours(24),
+                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), 
+                    SecurityAlgorithms.HmacSha256Signature)
+            };
+            
+            var token = tokenHandler.CreateToken(tokenDescriptor);
+            return tokenHandler.WriteToken(token);
+        }
+    }
+}";
    }

    private static string GetSampleControllerFile()
    {
        return @"using Microsoft.AspNetCore.Mvc;
using Authentication;

namespace Controllers
{
    [ApiController]
    [Route(""api/[controller]"")]
    public class AuthController : ControllerBase
    {
        private readonly JwtService _jwtService;
        
        public AuthController(JwtService jwtService)
        {
            _jwtService = jwtService;
        }
        
        [HttpPost(""login"")]
        public IActionResult Login([FromBody] LoginRequest request)
        {
            // TODO: Validate user credentials
            if (request.Username == ""admin"" && request.Password == ""password"")
            {
                var token = _jwtService.GenerateToken(request.Username, new[] { ""Admin"" });
                return Ok(new { Token = token });
            }
            
            return Unauthorized();
        }
    }
    
    public class LoginRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}";
    }

    private static string GetSampleControllerDiff()
    {
        return @"@@ -18,7 +18,12 @@
         [HttpPost(""login"")]
         public IActionResult Login([FromBody] LoginRequest request)
         {
-            // Hardcoded for demo
+            // TODO: Validate user credentials
+            if (request.Username == ""admin"" && request.Password == ""password"")
+            {
+                var token = _jwtService.GenerateToken(request.Username, new[] { ""Admin"" });
+                return Ok(new { Token = token });
+            }
             
             return Unauthorized();
         }";
    }

    private static string GetSampleControllerPrevious()
    {
        return @"using Microsoft.AspNetCore.Mvc;

namespace Controllers
{
    [ApiController]
    [Route(""api/[controller]"")]
    public class AuthController : ControllerBase
    {        
        [HttpPost(""login"")]
        public IActionResult Login([FromBody] LoginRequest request)
        {
            // Hardcoded for demo
            return Unauthorized();
        }
    }
    
    public class LoginRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}";
    }

    private static string GetSamplePythonTest()
    {
        return @"import pytest
import requests
import json

class TestAuthentication:
    def test_login_success(self):
        """"""Test successful login with valid credentials""""""
        payload = {
            ""username"": ""admin"",
            ""password"": ""password""
        }
        
        response = requests.post(""http://localhost:5000/api/auth/login"", 
                               json=payload)
        
        assert response.status_code == 200
        data = response.json()
        assert ""Token"" in data
        assert len(data[""Token""]) > 0
        
    def test_login_failure(self):
        """"""Test login failure with invalid credentials""""""
        payload = {
            ""username"": ""admin"",
            ""password"": ""wrongpassword""
        }
        
        response = requests.post(""http://localhost:5000/api/auth/login"", 
                               json=payload)
        
        assert response.status_code == 401";
    }

    private static string GetSamplePythonDiff()
    {
        return @"@@ -0,0 +1,28 @@
+import pytest
+import requests
+import json
+
+class TestAuthentication:
+    def test_login_success(self):
+        """"""Test successful login with valid credentials""""""
+        payload = {
+            ""username"": ""admin"",
+            ""password"": ""password""
+        }
+        
+        response = requests.post(""http://localhost:5000/api/auth/login"", 
+                               json=payload)
+        
+        assert response.status_code == 200
+        data = response.json()
+        assert ""Token"" in data
+        assert len(data[""Token""]) > 0
+        
+    def test_login_failure(self):
+        """"""Test login failure with invalid credentials""""""
+        payload = {
+            ""username"": ""admin"",
+            ""password"": ""wrongpassword""
+        }
+        
+        response = requests.post(""http://localhost:5000/api/auth/login"", 
+                               json=payload)
+        
+        assert response.status_code == 401";
    }
}