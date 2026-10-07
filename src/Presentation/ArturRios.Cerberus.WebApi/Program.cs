using ArturRios.Cerberus.WebApi.Operations;
using Microsoft.Extensions.Options;

namespace ArturRios.Cerberus.WebApi;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        string[] commands = ["--validate-configuration", "--migrate", "--reconcile-restore"];
        var requested = args.Where(commands.Contains).ToArray();
        if (requested.Length > 1)
        {
            Console.Error.WriteLine("configuration_invalid: Select one maintenance command.");
            return 1;
        }
        try
        {
            await using var application = Startup.CreateApplication(args.Except(requested).ToArray());
            if (requested.Length == 1)
                return await OperationalCommands.ExecuteAsync(application.Services, requested[0], default);
            if (await OperationalCommands.PrepareTrafficAsync(application.Services, default) != 0)
            {
                Console.Error.WriteLine("restore_reconciliation_required");
                return 1;
            }
            await application.RunAsync();
            return 0;
        }
        catch (HostAbortedException) { throw; } // WebApplicationFactory intercepts host construction.
        catch (OptionsValidationException exception)
        {
            foreach (var failure in exception.Failures) Console.Error.WriteLine(failure);
            return 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("startup_or_maintenance_failed");
            return 1;
        }
    }
}
