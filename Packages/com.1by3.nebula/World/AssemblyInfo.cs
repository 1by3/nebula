using System.Runtime.CompilerServices;

// RuntimeGrid (Nebula.Runtime) is the one caller allowed to move a scope's origin: it moves that scope's
// containers and entities first and then records the move on the frame (docs/scope-frames.md D3). The tests
// assert the same sequence.
[assembly: InternalsVisibleTo("Nebula.Runtime")]
[assembly: InternalsVisibleTo("Nebula.Tests.EditMode")]
[assembly: InternalsVisibleTo("Nebula.Tests.PlayMode")]
