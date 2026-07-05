CREATE PROCEDURE dbo.ProcessDay @full BIT AS BEGIN IF @full = 1 EXEC dbo.RecalcTotals; EXEC dbo.WriteAudit @msg = N'day'; END
