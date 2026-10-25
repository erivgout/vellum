<img src="assets/icon.svg" width="64" height="64" alt="">

# Vellum

A configurable electronic quality management system (eQMS) for document control, quality events, risk, supplier quality, audits, training and EHS.

- [System Requirements Specification](docs/requirements/README.md)
- [Architecture and developer guide](docs/architecture.md)

## Quick start

```powershell
docker compose up -d                  # SQL Server 2022 (localhost:14333)
dotnet run --project src/Vellum.Web   # http://localhost:5048
```

The first start creates the database, installs the packaged applications and seeds demo data.
Sign in as `jlee`, `sortiz`, `pshah` or `admin` with the password `Vellum!Demo2026`.

Built with ASP.NET Core (.NET 10, Razor Pages), Entity Framework Core on SQL Server, Bootstrap 5.3 and Bootstrap Icons.

Created in [T3 Code](https://t3.codes).
