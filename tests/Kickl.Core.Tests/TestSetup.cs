using System.Runtime.CompilerServices;
using Kickl.Core.Mail;

namespace Kickl.Core.Tests;

internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Init() => MailEndpoint.UseFreePortInstead = true;   // new mail endpoints default to port 25; parallel tests need their own
}
