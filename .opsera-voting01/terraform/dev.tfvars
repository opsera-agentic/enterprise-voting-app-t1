app_name    = "voting01"
environment = "dev"
aws_region  = "us-west-2"

# These values will be provided by the bootstrap workflow
# vpc_id              = "vpc-xxxxxxxxx"
# private_subnet_ids  = ["subnet-xxx", "subnet-yyy"]
# eks_oidc_provider_arn = "arn:aws:iam::ACCOUNT:oidc-provider/oidc.eks.us-west-2.amazonaws.com/id/XXXXXXXX"

eks_cluster_name = "opsera-usw2-np"

# Database sizing (dev - minimal)
rds_instance_class    = "db.t3.micro"
rds_allocated_storage = 20
redis_node_type       = "cache.t3.micro"
