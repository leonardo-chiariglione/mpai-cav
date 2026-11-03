# Bankruptcy datasets for CUI-CPP (training and testing the Prediction AIMs)

Public datasets of company financial ratios with a bankruptcy outcome. They are **development data**: the AIMs of CUI-CPP never
read them when they run. They are used to train and check the models that the Primary Default Prediction AIM (and, if data
exist, the others) read as Access data (a trained model named by URI and SHA-256 in `AccessSources`, with its Terms).
Downloaded 2026-11-02. The data files are **not committed** (size, third-party data): see `.gitignore`; fetch them again from
the addresses below and check them with `SHA256SUMS.txt`.

| Folder | Dataset | Source | Licence | Content |
|---|---|---|---|---|
| `Polish/` | Polish Companies Bankruptcy | https://archive.ics.uci.edu/static/public/365/polish+companies+bankruptcy+data.zip (page https://archive.ics.uci.edu/dataset/365/polish+companies+bankruptcy+data) | CC BY 4.0 | `1year.arff` to `5year.arff`: 64 ratios and a bankrupt flag; companies observed 1 to 5 years before the outcome (7,027 / 10,173 / 10,503 / 9,792 / 5,910 rows; 271 / 400 / 495 / 515 / 410 bankrupt). Bankrupt companies 2000-2012, operating companies 2007-2013. |
| `Taiwanese/` | Taiwanese Bankruptcy Prediction | https://archive.ics.uci.edu/static/public/572/taiwanese+bankruptcy+prediction.zip (page https://archive.ics.uci.edu/dataset/572/taiwanese+bankruptcy+prediction) | CC BY 4.0 | `data.csv`: 6,819 companies, 95 ratios, `Bankrupt?` flag (220 bankrupt); 1999-2009, from the Taiwan Economic Journal. |

SHA-256 of the archives as downloaded: `polish.zip` 17377929aa0b204bbf957e56462cf827c19fe4e2ce89f27dfbc77f9ea2bb16c9, `taiwan.zip` c346f5ad2618cb198e7ed8306cf2f31fe3bb2ec60acdbbe1736788d50f269aac.

## Attribution (CC BY 4.0: a model trained on these data must carry it in its Terms)

- Tomczak, S. (2016). Polish Companies Bankruptcy [Dataset]. UCI Machine Learning Repository. https://doi.org/10.24432/C5F600
- Taiwanese Bankruptcy Prediction [Dataset]. (2020). UCI Machine Learning Repository. https://doi.org/10.24432/C5004D

## Caution

Listed or manufacturing companies of Poland and Taiwan, with ratios that are not the Italian balance-sheet codes of the CUI-CPP Financial
Descriptors: a model trained on them needs a mapping and is a demonstration, not a model for real companies (see M3288).
