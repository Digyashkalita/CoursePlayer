using CoursePlayer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CoursePlayer.Data;

/// <summary>
/// Lets <c>dotnet ef</c> construct a context without starting the WPF application.
/// Points at the same database file the running app uses.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CoursePlayerDbContext>
{
    public CoursePlayerDbContext CreateDbContext(string[] args)
    {
        var paths = new AppPaths();
        var options = new DbContextOptionsBuilder<CoursePlayerDbContext>()
            .UseSqlite($"Data Source={paths.DatabasePath}")
            .Options;

        return new CoursePlayerDbContext(options);
    }
}
