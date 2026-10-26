import {
  ApolloClient,
  InMemoryCache,
  HttpLink,
  split,
} from '@apollo/client';
import { GraphQLWsLink } from '@apollo/client/link/subscriptions';
import { getMainDefinition } from '@apollo/client/utilities';
import { createClient } from 'graphql-ws';

const httpUrl = import.meta.env.VITE_GRAPHQL_HTTP_URL || '/graphql';
const wsUrl =
  import.meta.env.VITE_GRAPHQL_WS_URL ||
  `${window.location.protocol === 'https:' ? 'wss:' : 'ws:'}//${window.location.host}/graphql`;

const httpLink = new HttpLink({
  uri: httpUrl,
});

const wsLink = new GraphQLWsLink(
  createClient({
    url: wsUrl,
    retryAttempts: 10,
  })
);

const splitLink = split(
  ({ query }) => {
    const definition = getMainDefinition(query);
    return (
      definition.kind === 'OperationDefinition' &&
      definition.operation === 'subscription'
    );
  },
  wsLink,
  httpLink
);

export const apolloClient = new ApolloClient({
  link: splitLink,
  cache: new InMemoryCache({
    typePolicies: {
      OrderGql: {
        keyFields: ['id'],
      },
      ProductGql: {
        keyFields: ['id'],
      },
      MaterialGql: {
        keyFields: ['id'],
      },
    },
  }),
});
