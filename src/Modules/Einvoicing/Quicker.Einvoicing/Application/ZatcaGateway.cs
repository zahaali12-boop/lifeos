using Microsoft.Extensions.Options;
using Quicker.Einvoicing.Domain;
using Quicker.Einvoicing.Zatca;

namespace Quicker.Einvoicing.Application;

/// <summary>The API a device talks to: the in-process simulator for the simulator environment, the scheme's gateway for the others.</summary>
public sealed class ZatcaGateway(IHttpClientFactory httpClientFactory, ZatcaSimulator simulator, IOptions<ZatcaOptions> options)
{
    public const string HttpClientName = "zatca";

    public ZatcaApi For(EinvoiceDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Environment == DeviceEnvironments.Simulator)
        {
            return new ZatcaApi(new HttpMessageInvoker(simulator, disposeHandler: false), "https://zatca-simulator.local/e-invoicing/simulator");
        }

        if (!options.Value.BaseUrls.TryGetValue(device.Environment, out var baseUrl) || string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException($"No gateway address is configured for the '{device.Environment}' environment.");
        }

        return new ZatcaApi(httpClientFactory.CreateClient(HttpClientName), baseUrl.TrimEnd('/'));
    }
}
