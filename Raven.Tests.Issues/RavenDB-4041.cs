using System.Linq;
using System.Threading.Tasks;
using Raven.Abstractions.Data;
using Raven.Client;
using Raven.Client.Indexes;
using Raven.Tests.Helpers;
using Xunit;

namespace Raven.Tests.Issues
{
    public class RavenDB_4041 : RavenTestBase
    {
        [Fact]
        public void returns_metadata()
        {
            using (var store = NewDocumentStore())
            {
                var index = new Customers_ByName();
                index.Execute(store);

                using (var session = store.OpenSession())
                {
                    session.Store(new Customer { Name = "John", Address = "Tel Aviv" });
                    session.SaveChanges();
                }

                WaitForIndexing(store);

                using (var session = store.OpenSession())
                {
                    var customer = session.Query<Customer>().FirstOrDefault();
                    Assert.NotNull(customer);

                    var metadata = session.Advanced.GetMetadataFor(customer);
                    Assert.Equal(customer.Name, "John");
                    Assert.Equal(customer.Address, "Tel Aviv");

                    Assert.NotNull(metadata.Value<string>(Constants.RavenClrType));
                    Assert.NotNull(metadata.Value<string>(Constants.RavenEntityName));
                    Assert.NotNull(metadata.Value<string>(Constants.LastModified));
                    Assert.NotNull(metadata.Value<string>(Constants.RavenLastModified));
                }
            }
        }

        [Fact]
        public void returns_metadata_async()
        {
            using (var store = NewDocumentStore())
            {
                var index = new Customers_ByName();
                index.Execute(store);

                using (var session = store.OpenSession())
                {
                    session.Store(new Customer { Name = "John", Address = "Tel Aviv" });
                    session.SaveChanges();
                }

                WaitForIndexing(store);

                using (var session = store.OpenAsyncSession())
                {
                    var customerAsync = session.Query<Customer>().FirstOrDefaultAsync();
                    Assert.NotNull(customerAsync);

                    var customer = customerAsync.Result;
                    var metadata = session.Advanced.GetMetadataFor(customer);
                    Assert.Equal(customer.Name, "John");
                    Assert.Equal(customer.Address, "Tel Aviv");

                    Assert.NotNull(metadata.Value<string>(Constants.RavenClrType));
                    Assert.NotNull(metadata.Value<string>(Constants.RavenEntityName));
                    Assert.NotNull(metadata.Value<string>(Constants.LastModified));
                    Assert.NotNull(metadata.Value<string>(Constants.RavenLastModified));
                }
            }
        }

        [Fact]
        public void streaming_returns_metadata()
        {
            using (var store = NewRemoteDocumentStore(fiddler: true))
            {
                var index = new Customers_ByName();
                index.Execute(store);

                using (var session = store.OpenSession())
                {
                    session.Store(new Customer { Name = "John", Address = "Tel Aviv" });
                    session.SaveChanges();
                }

                WaitForIndexing(store);

                using (var session = store.OpenSession())
                {
                    var enumerator = session.Advanced.Stream<Customer>("customers/");

                    while (enumerator.MoveNext())
                    {
                        Assert.Equal("John", enumerator.Current.Document.Name);
                        Assert.Equal("Tel Aviv", enumerator.Current.Document.Address);

                        Assert.NotNull(enumerator.Current.Key);
                        Assert.NotNull(enumerator.Current.Etag);

                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.RavenClrType));
                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.RavenEntityName));
                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.LastModified));
                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.RavenLastModified));
                    }
                }
            }
        }

        [Fact]
        public async Task streaming_returns_metadata_async()
        {
            using (var store = NewDocumentStore())
            {
                var index = new Customers_ByName();
                index.Execute(store);

                using (var session = store.OpenSession())
                {
                    session.Store(new Customer { Name = "John", Address = "Tel Aviv" });
                    session.SaveChanges();
                }

                WaitForIndexing(store);

                using (var session = store.OpenAsyncSession())
                {
                    var enumerator = await session.Advanced.StreamAsync<Customer>("customers/");

                    while (await enumerator.MoveNextAsync())
                    {
                        Assert.Equal("John", enumerator.Current.Document.Name);
                        Assert.Equal("Tel Aviv", enumerator.Current.Document.Address);

                        Assert.NotNull(enumerator.Current.Key);
                        Assert.NotNull(enumerator.Current.Etag);

                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.RavenClrType));
                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.RavenEntityName));
                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.LastModified));
                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.RavenLastModified));
                    }
                }
            }
        }

        [Fact]
        public void streaming_query_returns_metadata()
        {
            using (var store = NewDocumentStore())
            {
                var index = new Customers_ByName();
                index.Execute(store);

                using (var session = store.OpenSession())
                {
                    session.Store(new Customer {Name = "John", Address = "Tel Aviv"});
                    session.SaveChanges();
                }

                WaitForIndexing(store);

                using (var session = store.OpenSession())
                {
                    var query = session.Query<Customer>(index.IndexName);
                    var enumerator = session.Advanced.Stream(query);

                    while (enumerator.MoveNext())
                    {
                        Assert.Equal("John", enumerator.Current.Document.Name);
                        Assert.Equal("Tel Aviv", enumerator.Current.Document.Address);

                        Assert.NotNull(enumerator.Current.Key);
                        Assert.NotNull(enumerator.Current.Etag);

                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.RavenClrType));
                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.RavenEntityName));
                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.TemporaryScoreValue));
                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.LastModified));
                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.RavenLastModified));
                    }
                }
            }
        }

        [Fact]
        public async Task streaming_query_returns_metadata_async()
        {
            using (var store = NewDocumentStore())
            {
                var index = new Customers_ByName();
                index.Execute(store);

                using (var session = store.OpenSession())
                {
                    session.Store(new Customer {Name = "John", Address = "Tel Aviv"});
                    session.SaveChanges();
                }

                WaitForIndexing(store);

                using (var session = store.OpenAsyncSession())
                {
                    var query = session.Query<Customer>(index.IndexName);
                    var enumerator = await session.Advanced.StreamAsync(query);

                    while (await enumerator.MoveNextAsync())
                    {
                        Assert.Equal("John", enumerator.Current.Document.Name);
                        Assert.Equal("Tel Aviv", enumerator.Current.Document.Address);

                        Assert.NotNull(enumerator.Current.Key);
                        Assert.NotNull(enumerator.Current.Etag);

                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.RavenClrType));
                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.RavenEntityName));
                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.TemporaryScoreValue));
                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.LastModified));
                        Assert.NotNull(enumerator.Current.Metadata.Value<string>(Constants.RavenLastModified));
                    }
                }
            }
        }

        public class Customer
        {
            public string Name { get; set; }
            public string Address { get; set; }
        }

        public class Customers_ByName : AbstractIndexCreationTask<Customer>
        {
            public Customers_ByName()
            {
                Map = customers => from customer in customers
                    select new
                    {
                        customer.Name
                    };
            }
        }
    }
}
 
