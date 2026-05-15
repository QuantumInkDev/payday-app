using System;

namespace PayDay.Models;

public sealed record PeriodBill(Bill Bill, DateTime? DueDate);
