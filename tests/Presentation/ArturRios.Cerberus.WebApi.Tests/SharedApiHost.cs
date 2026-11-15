using ArturRios.Configuration.Enums;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Functional;

namespace ArturRios.Cerberus.WebApi.Tests;

// ArturRios.Util.Test's WebApiTest builds its in-memory host in its constructor, and xUnit makes a new class instance
// for every test, so deriving a test class from it starts a whole API host per test. As a class fixture it starts one
// per test class. Every request still reads its state from the database and the identity fixture, so tests share
// nothing through the host itself; the client's default headers are cleared before each test (SharedApiTest).
public sealed class SharedApiHost() : WebApiTest<Program>(EnvironmentType.Local)
{
    internal HttpGateway Http => Gateway;
    internal void Reset() => Gateway.Client.DefaultRequestHeaders.Clear();
}

// The members a test class used to inherit from WebApiTest, over the class's shared host.
public abstract class SharedApiTest : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    protected SharedApiTest(SharedApiHost host)
    {
        _host = host;
        host.Reset();
    }

    protected HttpGateway Gateway => _host.Http;

    protected void Authorize(string authToken) => _host.Authorize(authToken);
}
