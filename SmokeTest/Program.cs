using System.Net;
using SmokeTest;

bool server = false;
int clientCount = 0;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case ("-c" or "--client") when i + 1 < args.Length && int.TryParse(args[i + 1], out var tmp):
            clientCount = tmp;
            break;
        case "-s":
        case "--server":
            server = true;
            break;
    }
}

var endpoint = new IPEndPoint(IPAddress.Loopback, 10000);
using var set = new EchoServer(new());
if (server)
{
    set.Listen(endpoint);
}

for (int i = 0; i < clientCount; i++)
{
    set.Connect(endpoint);
}