@{
    Solution = 'Duplicates.slnx'
    TestProjects = @(
        @{
            Path = 'Duplicates.Engine.Tests\Duplicates.Engine.Tests.csproj'
            Configuration = 'Debug'
        },
        @{
            Path = 'Duplicates.App.Tests\Duplicates.App.Tests.csproj'
            Configuration = 'Debug'
            Properties = @{
                Platform = 'x64'
            }
        }
    )
    BuildProjects = @(
        @{
            Path = 'Duplicates\Duplicates.csproj'
            Configuration = 'Debug'
            Properties = @{
                Platform = 'x64'
            }
        }
    )
}
