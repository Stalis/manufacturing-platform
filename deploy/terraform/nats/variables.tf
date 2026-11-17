variable "nats_servers" {
  description = "Comma-separated NATS server URLs for the target environment."
  type        = string
  nullable    = false

  validation {
    condition     = trimspace(var.nats_servers) != ""
    error_message = "nats_servers must specify at least one NATS server URL."
  }
}

variable "nats_credentials" {
  description = "Optional path to a NATS credentials file; supply through TF_VAR_nats_credentials."
  type        = string
  default     = null
}

variable "stream_replicas" {
  description = "Number of stream replicas supported by the target NATS cluster."
  type        = number
  default     = 1
  nullable    = false

  validation {
    condition     = var.stream_replicas >= 1 && var.stream_replicas <= 5 && floor(var.stream_replicas) == var.stream_replicas
    error_message = "stream_replicas must be an integer between 1 and 5."
  }
}
