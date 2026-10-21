# Who takes part in what: the lists the invitations are made from

`groups.json` - the groups: `Id` (the short name: MMM), `Name`, `Room` (the id of its meeting room in the MPAI building: `MeetingRoom` is the MMM one;
the others - `AIF-Room`, `AIH-Room`, ... one for each group - have no meeting set up yet).

`people.json` - the people: `Email`, `Name` (as it should appear), `Groups` (the ids of the groups they take part in), `Chair` (the ids of the groups they chair).
`Id` is optional: the name the person has in the M-Instance (letters only; by default the first name, made unique if two are alike).

```
{ "Email": "anna@example.org", "Name": "Anna Rossi", "Groups": ["MMM", "AIF"], "Chair": ["MMM"] }
```

A group meeting is started with `MMM\Run-Meeting.ps1 -Group MMM -When "Thursday 22 October, 17:00 CEST"`: the people of the group get a personal link each, by an email composed
through Outlook. The emails are sent only after you confirm at the console (or saved as Outlook drafts with `-Mail draft`).
