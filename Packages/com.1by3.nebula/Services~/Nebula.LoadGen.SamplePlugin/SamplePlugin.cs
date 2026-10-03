using System.Numerics;
using Nebula.LoadGen;

namespace Nebula.LoadGen.SamplePlugin;

/// <summary>
/// A tiny plugin: the <c>strafe</c> behaviour walks back and forth along x and presses action bit 0 on every turn.
/// Options: <c>length</c> (meters each way, 10) and <c>bit</c> (action bit, 0). Run it with
/// <c>--plugin Nebula.LoadGen.SamplePlugin.dll --mix strafe:50,idle:50 --opt strafe.length=20</c>.
/// A real game's plugin has the same shape: one <see cref="ILoadGenPlugin"/> class, one class per behaviour, and
/// optionally an <see cref="IInputEncoder"/> and a <see cref="ScopeTravelHandler"/>.
/// </summary>
public sealed class SamplePlugin : ILoadGenPlugin
{
    public void Register(IBehaviourRegistry registry)
    {
        registry.Add("strafe", options => new Strafe(options));
        // registry.SetInputEncoder(new MyGameInputEncoder());   // write the game's own input bytes
        // registry.SetScopeTravel((client, target) => { ... client.SendServerRpc(0, 0, "RequestTravel", payload); return true; });
    }
}

public sealed class Strafe : ILoadGenBehaviour
{
    private readonly double _length;
    private readonly uint _bit;
    private double _travelled;
    private float _direction = 1;

    public Strafe(BehaviourOptions options)
    {
        _length = options.GetDouble("length", 10);
        _bit = 1u << options.GetInt("bit", 0);
    }

    public void OnJoined(IClientContext client) { _travelled = 0; _direction = 1; }

    public void Tick(IClientContext client, double dt)
    {
        if (!client.Joined) return;
        client.SetMove(new Vector2(_direction, 0));
        _travelled += dt * 5; // the loadgen's default --move-speed
        if (_travelled >= _length)
        {
            _travelled = 0;
            _direction = -_direction;
            client.SetActions(_bit);
            client.CountAction();
        }
        else client.SetActions(0);
    }
}
