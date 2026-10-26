terraform {
  required_providers {
    jetstream = {
      source  = "nats-io/jetstream"
      version = "~> 0.4.0"
    }
  }
}

provider "jetstream" {
  servers = "nats.localhost:4222"
}