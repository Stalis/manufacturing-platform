terraform {
  required_version = ">= 1.7.0, < 2.0.0"

  # shortcut: local state is single-host; use a remote backend before shared deployments.
  backend "local" {}

  required_providers {
    jetstream = {
      source  = "nats-io/jetstream"
      version = "~> 0.4.0"
    }
  }
}

provider "jetstream" {
  servers     = var.nats_servers
  credentials = var.nats_credentials
}
