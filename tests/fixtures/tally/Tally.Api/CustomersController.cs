using System;
using System.Collections.Generic;
using Tally.Domain;

namespace Tally.Api
{
    public class CustomerRequest
    {
        public const int MaxNameLength = 80;

        public string Name { get; set; }
        public string Email { get; set; }
    }

    public class CustomersController
    {
        readonly Dictionary<string, Customer> _customers = new Dictionary<string, Customer>();

        public Customer Create(CustomerRequest request, string role)
        {
            if (role == "accountant") throw new UnauthorizedAccessException("Accountants can't add customers.");
            if (request.Name.Length > CustomerRequest.MaxNameLength) throw new ArgumentException("Name is too long.");
            var c = new Customer { Id = Guid.NewGuid().ToString("N"), Name = request.Name, Email = request.Email };
            _customers[c.Id] = c;
            return c;
        }

        public string Welcome(Customer customer)
        {
            return "Welcome to Tally, " + customer.DisplayName() + ". Your first invoice is on us.";
        }

        public IEnumerable<Customer> All() => _customers.Values;
    }
}
