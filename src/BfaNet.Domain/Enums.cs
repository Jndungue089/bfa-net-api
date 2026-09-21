namespace BfaNet.Domain;

public enum CustomerStatus { Active, Locked, Closed }

public enum AccountType { Ordem, Ordenado, Poupanca, Bankita, Interna }

public enum AccountStatus { Active, Frozen, Closed }

public enum Currency { AOA, USD, EUR }

public enum LedgerDirection { Debit, Credit }

public enum TransactionKind { Transfer, ServicePayment, TopUp, StatePayment, Deposit, Fee }

public enum TransactionStatus { Completed, Failed, Reversed }

public enum CardProduct { Debito, PrePago, Credito }

public enum CardStatus { Active, Blocked, Cancelled }
