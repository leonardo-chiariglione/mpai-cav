# Live Theatrical Performance

Runs a show in a Real and a Virtual venue at once (XRV-LTP of MPAI-XRV V1.0). Cue by Cue, the Script decides what the lights, the sound and the virtual stage do, as the performers, the participants and the operators act.

| Part | What |
|---|---|
| Workflow | `XRV-LTP.orch`: one Controller, one Module, the Live Theatrical Performance Composite AIM `1XRV-LTP-V1.0-I01`, run continuously |
| User Agent | `AIF/UserAgent/Ua` (`Mpai.Ua`): reads `XRV-LTP.orch`, binds the devices Show, RealVenue and VirtualVenue to the boundary Ports, and talks to the Controller through the Controller API only |
| Module | the seven AIMs of XRV-LTP (`AIMs/XRV1/V1.0`): Environment Description, Performance, Participant and Operator Status Interpretation, Action Generation, RE and VE Experience Generation |
| Output Units | the devices that execute RE Data Out and VE Data Out. In this first slice they are stand-ins, in the test only (`Test/Mpai.Aif.Tests/XrvLtpWorkflowTests.cs`): a DMX writer for the lighting. The virtual venue is MMM-TEC V2.2 (`VirtualVenue/MmmVirtualVenue.cs`, over the MMM-API of `D:\AI\MMM`): the virtual stage is an M-Location of an M-Instance and the show is a collection of Processes in it (the Users of performers and participants, and whatever animates Items); the device is the Process that animates the virtual venue: it places the Virtual Objects with MM-Add, sets a lamp with Property Change (Light Source, Intensity) and gives as VE Data In who comes onto the stage, from MM-Capture. No real hardware, no MIDI |

## The devices

| Device | Gives (stream) | Receives (deliver) |
|---|---|---|
| Show | Script, Action Table, Script-Venue Associations, RE and VE Venue Specifications, Participant Data | Participant Data |
| RealVenue | RE Data In | RE Data Out |
| VirtualVenue | VE Data In | VE Data Out |

## Not yet

- The App program (an executable with its own AIM host, like the other Apps): not written. The workflow runs in the tests only.
- Real Output Units (a DMX interface, MIDI, stage machinery). The virtual venue on MMM-TEC changes only Intensity (Light Source); other VE properties are not performed (said in Unperformed).

## Shows to simulate

- The mini show (`Test/Data/XRV/MiniShow`): three Cues, Real, Virtual, Mixed.
- The Lamplighter (`Test/Data/XRV/Lamplighter/Script.json`, with the other documents of the mini show): two Cues, machine-driven, no operator. C1 (Mixed): Anna on the real stage and a guest on the virtual stage - real lamps up, virtual lamp at 30%. C2 (Virtual): Bot, the virtual double of Anna, an MMM Process that walks onto the virtual stage by itself a second after it sees the lamp lit - real lamps off, virtual lamp full; the show ends.
