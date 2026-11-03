# Company Performance Prediction - the App (MPAI-CUI CPP V2.1)

A **User Agent** that is a console program (`Ua/CuiUa`) and a **Controller** that is a program of its own (`Controller/CuiController`). The User Agent runs the workflow `CUI-CPP.orch`
with the interpreter of the User Agent; it supplies only devices - the data of a company, read from a file, and a report on the screen - and knows no AIM. The Controller starts the
Module (`1CUI-CPP-V2.1-I01`) and its AIMs as the L3 says.

    CuiController [--listen http://127.0.0.1:5061] [--model <FinancialDefault-Polish5y.json>]
    CuiUa --company <company.json> [--horizon <months>] [--controller <url>] [--wait <seconds>] [--json <out.json>] [--verbose]

`Cui.bat Samples\distressed.json` runs both. The company file is one JSON object: `PredictionHorizon` (months), `FinancialDescriptors` (CUI-FND), `GovernanceDescriptors` (CUI-GVS) and,
optionally, `ClimateRiskDescriptors` (CUI-LRD), `CyberRiskDescriptors` (CUI-CRD), `SecondaryRiskStatements` (CUI-SRS). `Samples/` holds a healthy and a distressed company (the ones the tests use).

What the Module gives is printed with the explanation each descriptor carries: the probability of default and the importance of each financial descriptor to it; what the organisation does to it;
the probability of discontinuity and what each risk contributes; the Secondary Discontinuity Probability.

**The model.** The Prediction AIMs use a built-in reference model, or a trained one that is Access data: `AIMs/CUI/V2.1/Models/FinancialDefault-Polish5y.json`, trained on the Polish Companies
Bankruptcy dataset (CC BY 4.0) by `AIMs/CUI/Tools/Mpai.Cui.Train`. The L3 of the AIMs gives its address (on GitHub, valid once the branch is merged) and its SHA-256; `--model` gives the Controller a
file on this machine to obtain it from instead. A demonstration model, trained on companies of Poland: **not for real companies**.

The data cross the Controller API as the JSON instance of their schema (`Mpai.Aif.PortData`: a codec for each CUI Data Type and for `number`; what crosses is checked against the published schema).
