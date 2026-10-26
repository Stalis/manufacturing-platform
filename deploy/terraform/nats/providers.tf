terraform {
  required_providers {
    jetstream = {
      source  = "nats-io/jetstream"
      version = "~> 0.4.0"
    }
  }
}

variable "nats_servers" {
  type    = string
  default = "nats.localhost:4222"
}

provider "jetstream" {
  servers = var.nats_servers
}
