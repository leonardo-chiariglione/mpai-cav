"""Writes the mini show of XRV-LTP V1.0 (stage S1) into D:\\AI\\Test\\Data\\XRV\\MiniShow.

Three Cues: C1 Real, C2 Virtual, C3 Mixed. Two DMX fixtures (FX1, FX2) in the Real Venue, one virtual lamp (VLamp1) in the Virtual Venue.
Run: python -I make_minishow.py   (the tests validate every file against the schemas of D:\\AI\\schemas\\XRV1\\V1.0)
"""
import json, os

UE, MI = "UE1", "MI1"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "MiniShow")


def t(name):
    return {"Header": "OSD-STM-V1.5", "SimpleTimeID": name,
            "SimpleTimeData": [{"FlagsByte": 3, "StartTime": 0, "EndTime": 0, "AccuracyMode": "single", "AccuracyPlusMinus": 0, "TimeType": True, "TimeUnit": "01"}]}


def status(key, header, ident, data, data_key=None):
    return {"Header": header, key + "ID": ident, "UEnvironmentID": UE, key + "Time": t(ident + "-t"), (data_key or key + "Data"): data}


# the targets of the Cue Conditions
anna = status("REPerformanceStatus", "XRV-RFS-V1.0", "C1-target", {"IndividualStatuses": [{"REPerformerID": "Anna"}]})
guest1 = status("VEParticipantStatus", "XRV-VTS-V1.0", "C2a-target", {"IndividualStatuses": [{"VEParticipantID": "Guest1"}]})
guest2 = status("VEParticipantStatus", "XRV-VTS-V1.0", "C2b-target", {"IndividualStatuses": [{"VEParticipantID": "Guest2"}]})
seat = status("REParticipantStatus", "XRV-RTS-V1.0", "C3-target-re", {"IndividualStatuses": [{"REParticipantID": "P1", "SeatID": "S1"}]})
bot = status("VEPerformanceStatus", "XRV-VFS-V1.0", "C3-target-ve", {"IndividualStatuses": [{"VEPerformerID": "Bot"}]})

cue_list = [
    {"Cue": [{"RealCue": {"RECueID": "C1", "REGroups": [
        {"RECueConditions": {"REPerformanceStatus": anna}, "REActions": ["RE-LIGHTS-UP"], "VEActions": [], "NextCue": "C2"}]}}]},
    {"Cue": [{"VirtualCue": {"VECueID": "C2", "VEGroups": [
        {"VECueConditions": {"VEParticipantStatus": guest1}, "REActions": [], "VEActions": ["VE-LAMP-ON"], "NextCue": "C3"},
        {"VECueConditions": {"VEParticipantStatus": guest2}, "REActions": [], "VEActions": ["VE-LAMP-DIM"], "NextCue": "C3"}]}}]},
    {"Cue": [{"MixedCue": {"MECueID": "C3", "MEGroups": [
        {"RECueConditions": {"REParticipantStatus": seat}, "VECueConditions": {"VEPerformanceStatus": bot},
         "REActions": ["RE-LIGHTS-OFF"], "VEActions": ["VE-LAMP-OFF"]}]}}]},
]

script = {
    "Header": "XRV-SCR-V1.0", "MInstanceID": MI, "UEnvironmentID": UE, "ScriptID": "MiniShow-Script", "ScriptTime": t("script-t"),
    "ShowHeader": "Mini show", "TitleOfProduction": "Mini show", "ShowDescription": "Three Cues: Real, Virtual, Mixed.",
    "Characters": [{"Name": "Anna", "Nickname": "A", "CharacterID": {"Real": "Anna", "Virtual": "Bot"}, "Mnemonic": "ANN"}],
    "TheatricalSetList": [{"TheatricalSet": [{"Name": "Stage", "SetID": {"Real": "Stage", "Virtual": "VStage"}, "Mnemonic": "STG"}],
                           "Objects": [{"Name": "Lamp 1", "ObjectID": {"Real": "FX1", "Virtual": "VLamp1"}, "ObjectType": "Static", "Mnemonic": "L1"},
                                       {"Name": "Lamp 2", "ObjectID": {"Real": "FX2"}, "ObjectType": "Static", "Mnemonic": "L2"}]}],
    "Participants": [{"MetaName": "Guest", "Nickname": "G", "Mnemonic": "GST"}],
    "CueList": cue_list,
}


def dmx(channel, value):
    return {"ActionCommand": {"Protocol": "DMX", "Universe": 1, "Channel": channel, "Value": value}, "ActionData": {}}


def lamp(value):
    return {"ActionCommand": {"Object": "VLamp1", "Property": "Intensity", "Value": value}, "ActionData": {}}


action_table = {
    "Header": "XRV-ATB-V1.0", "MInstanceID": MI, "UEnvironmentID": UE, "ActionTableID": "MiniShow-Actions", "ActionTableTime": t("atb-t"),
    "ActionTableData": [
        {"ActionID": "RE-LIGHTS-UP", "ActionName": "Both lamps to full", "Action": [dmx(1, 255), dmx(2, 255)]},
        {"ActionID": "RE-LIGHTS-OFF", "ActionName": "Both lamps off", "Action": [dmx(1, 0), dmx(2, 0)]},
        {"ActionID": "VE-LAMP-ON", "ActionName": "Virtual lamp to full", "Action": [lamp(1.0)]},
        {"ActionID": "VE-LAMP-DIM", "ActionName": "Virtual lamp to 30%", "Action": [lamp(0.3)]},
        {"ActionID": "VE-LAMP-OFF", "ActionName": "Virtual lamp off", "Action": [lamp(0.0)]},
    ],
}

re_venue = {
    "Header": "XRV-RVS-V1.0", "UEnvironmentID": UE, "REVenueSpecificationID": "MiniShow-REVenue", "REVenueSpecificationTime": t("rvs-t"),
    "DMXUniverses": [{"UniverseID": 1, "Protocol": "DMX", "Channels": [
        {"ChannelNumber": 1, "ChannelDefinition": "Dimmer", "FixtureID": "FX1"},
        {"ChannelNumber": 2, "ChannelDefinition": "Dimmer", "FixtureID": "FX2"}]}],
}

ve_venue = {
    "Header": "XRV-VVS-V1.0", "MInstanceID": MI, "UEnvironmentID": UE, "VEVenueSpecificationID": "MiniShow-VEVenue", "VEVenueSpecificationTime": t("vvs-t"),
    "VirtualObjects": [{"Header": "OSD-3DO-V1.5", "MInstanceID": MI, "3DModelObjectID": "VLamp1", "3DModelObjectSpaceTime": t("vlamp1-st")}],
}

associations = {
    "Header": "XRV-SVA-V1.0", "MInstanceID": MI, "UEnvironmentID": UE, "ScriptVenueAssociationsID": "MiniShow-Associations", "ScriptVenueAssociationsTime": t("sva-t"),
    "REAssociations": {
        "PerformanceDesignations": {"Characters": [{"Name": "Anna", "Mnemonic": "ANN", "VenueElementID": "Anna", "DataSourceQualifier": "performer"}],
                                    "TheatricalSetList": [{"Objects": [{"Name": "Lamp 1", "VenueElementID": "FX1", "DataSinkQualifier": "DMX:1:1"},
                                                                       {"Name": "Lamp 2", "VenueElementID": "FX2", "DataSinkQualifier": "DMX:1:2"}]}],
                                    "PerformanceStatusElements": [{"StatusElementID": "Presence", "Parameters": ["REPerformerID"], "DataSourceQualifier": "performer"}]},
        "ParticipantDesignations": {"Participants": [{"MetaName": "Guest", "VenueElementID": "P1", "DataSourceQualifier": "seat"}],
                                    "ParticipantStatusElements": [{"StatusElementID": "SeatId", "Parameters": ["SeatID"], "DataSourceQualifier": "seat"}]},
        "ActionElements": [{"ActionID": "RE-LIGHTS-UP", "Parameters": ["Value"], "DataSinkQualifier": "DMX:1"},
                           {"ActionID": "RE-LIGHTS-OFF", "Parameters": ["Value"], "DataSinkQualifier": "DMX:1"}]},
    "VEAssociations": {
        "PerformanceDesignations": {"Characters": [{"Name": "Anna", "Mnemonic": "ANN", "VenueElementID": "Bot"}],
                                    "TheatricalSetList": [{"Objects": [{"Name": "Lamp 1", "VenueElementID": "VLamp1", "DataSinkQualifier": "VLamp1"}]}]},
        "ParticipantDesignations": {"Participants": [{"MetaName": "Guest", "VenueElementID": "Guest1"}]},
        "ActionElements": [{"ActionID": "VE-LAMP-ON", "Parameters": ["Intensity"], "DataSinkQualifier": "VLamp1"},
                           {"ActionID": "VE-LAMP-DIM", "Parameters": ["Intensity"], "DataSinkQualifier": "VLamp1"},
                           {"ActionID": "VE-LAMP-OFF", "Parameters": ["Intensity"], "DataSinkQualifier": "VLamp1"}]},
}

participant_data = {
    "Header": "XRV-PTD-V1.0", "MInstanceID": MI, "UEnvironmentID": UE, "ParticipantDataID": "MiniShow-PD-1", "ParticipantDataTime": t("ptd-t"), "ParticipantID": "P1",
    "ParticipantAttendanceHistory": [{"VisitNumber": 2, "DateOfVisit": ["2026-10-01", "2026-11-07"], "TicketClass": "Stalls", "ParticipantCategory": "Adult",
                                      "ParticipantAttributes": [{"AttributeID": "A1", "AttributeMnemonic": "LANG", "AttributeValue": "it"}]}],
}

show_descriptors = {
    "Header": "XRV-SHD-V1.0", "MInstanceID": MI, "UEnvironmentID": UE, "ShowDescriptorsID": "MiniShow-SHD-1", "ShowDescriptorsTime": t("shd-t"),
    "ShowDescriptors": {"REPerformanceStatus": anna, "VEParticipantStatus": guest1, "REParticipantStatus": seat, "VEPerformanceStatus": bot},
}

files = {
    "Script.json": script, "ActionTable.json": action_table, "REVenueSpecification.json": re_venue, "VEVenueSpecification.json": ve_venue,
    "ScriptVenueAssociations.json": associations, "ParticipantData.json": participant_data, "ShowDescriptors.json": show_descriptors,
}

os.makedirs(OUT, exist_ok=True)
for name, datum in files.items():
    with open(os.path.join(OUT, name), "w", encoding="utf-8", newline="\n") as f:
        json.dump(datum, f, indent=2, ensure_ascii=False)
        f.write("\n")
print("written", len(files), "files to", OUT)
