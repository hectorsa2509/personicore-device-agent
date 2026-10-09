using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Agent;

/// <summary>All activation paths, including automatic resume, require current OS trust.</summary>
public sealed class CertificateGuardProxy(IProxySwitch inner, Func<bool> isTrusted) : IProxySwitch
{
    private readonly object _gate = new();
    private bool _blocked;
    public bool Wanted => inner.Wanted;
    public string StatusText => _blocked
        ? "error · " + AgentText.CertificateRequired
          + (inner.StatusText.StartsWith("error", StringComparison.Ordinal)
              ? " " + AgentText.CertificateRestoreFailed : string.Empty)
        : inner.StatusText;

    public void SetWanted(bool wanted, string? nodeIp, ProxyPause pause = ProxyPause.NoNode)
    {
        lock (_gate)
        {
            if (wanted && !CheckTrust()) { Block(nodeIp, pause); return; }
            _blocked = false;
            inner.SetWanted(wanted, nodeIp, pause);
        }
    }

    public void Sync(string? nodeIp, ProxyPause pause = ProxyPause.NoNode)
    {
        lock (_gate)
        {
            if (inner.Wanted && !CheckTrust()) { Block(nodeIp, pause); return; }
            // A blocked attempt stays off until an explicit retry, even after installation.
            if (_blocked) inner.SetWanted(false, nodeIp, pause);
            else inner.Sync(nodeIp, pause);
        }
    }

    public void PauseForExit()
    {
        lock (_gate) inner.PauseForExit();
    }

    private bool CheckTrust()
    {
        try { return isTrusted(); }
        catch (Exception ex)
        {
            Log.Error("Could not verify HTTPS certificate trust; proxy activation blocked", ex);
            return false;
        }
    }

    private void Block(string? nodeIp, ProxyPause pause)
    {
        _blocked = true;
        // Use the platform's ownership-aware restoration; never overwrite another proxy.
        inner.SetWanted(false, nodeIp, pause);
        Log.Warn(AgentText.CertificateRequired);
    }
}
