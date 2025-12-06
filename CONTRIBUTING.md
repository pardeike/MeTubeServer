# Contributing to MeTube Hub Server

Thank you for your interest in contributing to the MeTube Hub Server! This document provides guidelines and information for contributors.

## Getting Started

### Prerequisites
- .NET 9.0 SDK or later
- Git
- A code editor (Visual Studio, VS Code, or Rider)
- Basic understanding of ASP.NET Core and Entity Framework Core

### Development Setup

1. Fork the repository
2. Clone your fork:
   ```bash
   git clone https://github.com/YOUR_USERNAME/MeTubeServer.git
   cd MeTubeServer
   ```

3. Set up configuration:
   ```bash
   cp .env.example .env
   # Edit .env with your YouTube API key and callback URL
   ```

4. Run the application:
   ```bash
   dotnet run
   ```

## Project Structure

```
MeTubeServer/
├── BackgroundJobs/       # Background services for maintenance and reconciliation
├── Data/                 # EF Core DbContext and database configuration
├── Models/              # Entity models and DTOs
├── Services/            # Business logic services (YouTube API, WebSub, etc.)
├── Program.cs           # Application entry point and endpoint definitions
├── appsettings.json     # Configuration
└── README.md            # Documentation
```

## Code Style

- Follow standard C# naming conventions
- Use meaningful variable and method names
- Add XML documentation comments for public APIs
- Keep methods focused and concise
- Use async/await for I/O operations
- Follow SOLID principles

### Example

```csharp
/// <summary>
/// Fetches the uploads playlist ID for a YouTube channel.
/// </summary>
/// <param name="channelId">The YouTube channel ID</param>
/// <param name="cancellationToken">Cancellation token</param>
/// <returns>The uploads playlist ID, or null if not found</returns>
public async Task<string?> GetUploadsPlaylistIdAsync(
    string channelId,
    CancellationToken cancellationToken = default)
{
    // Implementation
}
```

## Making Changes

### Feature Development

1. Create a feature branch:
   ```bash
   git checkout -b feature/your-feature-name
   ```

2. Make your changes
3. Test your changes thoroughly
4. Commit with clear messages:
   ```bash
   git commit -m "Add feature: description of what you added"
   ```

### Bug Fixes

1. Create a bugfix branch:
   ```bash
   git checkout -b bugfix/issue-description
   ```

2. Fix the bug
3. Add tests to prevent regression
4. Commit with clear messages:
   ```bash
   git commit -m "Fix: description of what you fixed"
   ```

## Testing

### Manual Testing

1. Start the application
2. Test API endpoints with curl or Postman
3. Verify database state with sqlite3
4. Check logs for errors

### Example Test Scenarios

```bash
# Register channels
curl -X POST http://localhost:5000/api/users/testuser/channels \
  -H "Content-Type: application/json" \
  -d '{"channelIds": ["UCXuqSBlHAE6Xw-yeJA0Tunw"]}'

# Fetch feed
curl http://localhost:5000/api/users/testuser/feed

# Test WebSub verification
curl "http://localhost:5000/websub/youtube?hub.mode=subscribe&hub.topic=https://www.youtube.com/feeds/videos.xml?channel_id=UCXuqSBlHAE6Xw-yeJA0Tunw&hub.challenge=test123&hub.lease_seconds=864000"
```

## Pull Request Process

1. Ensure your code builds without warnings
2. Update documentation if needed
3. Create a pull request with a clear description:
   - What problem does this solve?
   - How did you test it?
   - Any breaking changes?

### Pull Request Template

```markdown
## Description
Brief description of the changes

## Type of Change
- [ ] Bug fix
- [ ] New feature
- [ ] Breaking change
- [ ] Documentation update

## Testing
How did you test these changes?

## Checklist
- [ ] Code builds without warnings
- [ ] Documentation updated (if needed)
- [ ] Tested manually
- [ ] No breaking changes (or documented if unavoidable)
```

## Areas for Contribution

### High Priority
- Unit and integration tests
- Health check endpoints
- Metrics and monitoring (Prometheus/OpenTelemetry)
- Rate limiting middleware
- Authentication for app endpoints

### Medium Priority
- Video metadata enrichment improvements
- Better error handling and retry logic
- Migration guide for PostgreSQL
- Performance optimizations
- Logging enhancements

### Nice to Have
- Admin dashboard
- Webhooks for video notifications
- Support for other video platforms
- GraphQL API
- Kubernetes deployment manifests

## Reporting Issues

When reporting issues, please include:
- **Description**: Clear description of the problem
- **Steps to Reproduce**: Detailed steps to reproduce the issue
- **Expected Behavior**: What you expected to happen
- **Actual Behavior**: What actually happened
- **Environment**: OS, .NET version, deployment method
- **Logs**: Relevant log excerpts

### Example Issue

```markdown
## Description
WebSub notifications are not being received

## Steps to Reproduce
1. Register a channel via POST /api/users/testuser/channels
2. Publish a new video on that channel
3. Wait 5 minutes
4. Check feed - new video is not present

## Expected Behavior
New video should appear in feed within seconds

## Actual Behavior
Video only appears after reconciliation job runs (30 minutes)

## Environment
- OS: Ubuntu 22.04
- .NET: 9.0.0
- Deployment: Docker Compose

## Logs
[Paste relevant logs here]
```

## Development Guidelines

### Database Changes

- Always test database changes thoroughly
- Consider migration path for existing deployments
- Update DbContext and entity models accordingly
- Document any schema changes

### Adding New Endpoints

1. Define the endpoint in `Program.cs`
2. Add DTOs to `Models/Dtos.cs` if needed
3. Update README.md with API documentation
4. Add examples to EXAMPLES.md
5. Test thoroughly

### Adding New Services

1. Create service in `Services/` directory
2. Register in `Program.cs` dependency injection
3. Add unit tests
4. Document public methods

### Background Jobs

- Keep jobs idempotent
- Add proper error handling and logging
- Consider configurable intervals
- Test with various scenarios (database errors, API failures, etc.)

## Code Review Guidelines

When reviewing pull requests:
- Check for code quality and style
- Verify tests cover new functionality
- Ensure documentation is updated
- Look for potential security issues
- Test the changes locally if possible

## Security

If you discover a security vulnerability:
1. **Do NOT** open a public issue
2. Email the maintainers directly
3. Include detailed information about the vulnerability
4. Wait for confirmation before disclosing

## Questions?

- Open a discussion on GitHub
- Check existing issues and pull requests
- Review the documentation (README.md, EXAMPLES.md)

## License

By contributing, you agree that your contributions will be licensed under the same license as the project (see LICENSE file).

---

Thank you for contributing to MeTube Hub Server! 🎉
