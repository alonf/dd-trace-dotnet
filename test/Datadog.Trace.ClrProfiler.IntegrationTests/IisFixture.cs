using System;
using System.Diagnostics;
using System.Threading;
using Datadog.Trace.TestHelpers;

namespace Datadog.Trace.ClrProfiler.IntegrationTests
{
    public sealed class IisFixture : IDisposable
    {
        private Process _iisExpress;

        public MockTracerAgent Agent { get; private set; }

        public int HttpPort { get; private set; }

        public void TryStartIis(TestHelper helper)
        {
            lock (this)
            {
                if (_iisExpress == null)
                {
                    var initialAgentPort = TcpPortProvider.GetOpenPort();
                    Agent = new MockTracerAgent(initialAgentPort);

                    HttpPort = TcpPortProvider.GetOpenPort();

                    // start IIS Express and give it a few seconds to boot up
                    _iisExpress = helper.StartIISExpress(Agent.Port, HttpPort);
                    Thread.Sleep(TimeSpan.FromSeconds(3));
                }
            }
        }

        public void Dispose()
        {
            Agent?.Dispose();

            if (_iisExpress != null)
            {
                if (!_iisExpress.HasExited)
                {
                    // sending "Q" to standard input does not work because
                    // iisexpress is scanning console key press, so just kill it.
                    // maybe try this in the future:
                    // https://github.com/roryprimrose/Headless/blob/master/Headless.IntegrationTests/IisExpress.cs
                    _iisExpress.Kill();
                    _iisExpress.WaitForExit();
                }

                _iisExpress.Dispose();
            }
        }
    }
}
